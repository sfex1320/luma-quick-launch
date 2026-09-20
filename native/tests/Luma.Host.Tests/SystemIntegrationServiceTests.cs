using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Microsoft.Win32;
using Xunit;

namespace Luma.Host.Tests;

public sealed class SystemIntegrationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "luma-system", Guid.NewGuid().ToString("N"));
    private readonly string _registry = @"Software\Luma.Tests\" + Guid.NewGuid().ToString("N");
    private string RunKey => _registry + @"\Run";
    private string ApprovedKey => _registry + @"\Approved";
    private string Exe => Path.Combine(_root, "portable 空格", "Luma.exe");
    private string Desktop => Path.Combine(_root, "desktop");
    private string Link => Path.Combine(Desktop, "Luma Quick Launch.lnk");
    private SystemIntegrationService Service(string? exe = null) => new(exe ?? Exe, RunKey, Desktop, ApprovedKey);

    public SystemIntegrationServiceTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Exe)!);
        Directory.CreateDirectory(Desktop);
        File.WriteAllText(Exe, "test executable placeholder; never launched");
    }

    [Fact]
    public void ReadsDoNotCreateAnyEntryAndEnableDisableUsesQuotedCurrentExecutable() => Sta(() =>
    {
        Assert.Equal(new(false, false, false), Service().GetIntegration());
        Assert.Null(Registry.CurrentUser.OpenSubKey(RunKey));
        Assert.Empty(Directory.GetFiles(Desktop));
        Assert.Equal(new(true, true, false), Service().SetAutoStart(true));
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
            Assert.Equal($"\"{Exe}\" --startup", run!.GetValue("LumaQuickLaunch"));
        Assert.Equal(new(false, false, false), Service().SetAutoStart(false));
        using var after = Registry.CurrentUser.OpenSubKey(RunKey);
        Assert.Null(after!.GetValue("LumaQuickLaunch"));
    });

    [Fact]
    public void DisableCannotDeleteOtherCopiesButExplicitEnableReplacesThem() => Sta(() =>
    {
        var other = Path.Combine(_root, "other", "Luma.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.Copy(Exe, other);
        Service(other).SetAutoStart(true);
        Assert.Equal(new(true, false, false), Service().GetIntegration());
        Assert.Equal(new(true, false, false), Service().SetAutoStart(false));
        Assert.True(Service(other).GetIntegration().AutoStartHere);
        Service().SetAutoStart(true);
        Assert.False(Service(other).GetIntegration().AutoStartHere);
        Assert.True(Service().GetIntegration().AutoStartHere);
    });

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void RespectsWindowsStartupApprovalAndOnlyExplicitEnableClearsMarker(int status, bool enabled) => Sta(() =>
    {
        Service().SetAutoStart(true);
        var marker = new byte[12];
        marker[0] = (byte)status;
        using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        approved.SetValue("LumaQuickLaunch", marker, RegistryValueKind.Binary);
        Assert.Equal(new(enabled, enabled, false), Service().GetIntegration());
        Assert.Equal(marker, approved.GetValue("LumaQuickLaunch"));
        Assert.True(Service().SetAutoStart(true).AutoStartHere);
        Assert.Null(approved.GetValue("LumaQuickLaunch"));
    });

    [Fact]
    public void OverlongRunCommandIsRejectedWithoutReplacingExistingRegistration() => Sta(() =>
    {
        Service().SetAutoStart(true);
        var longPath = Path.Combine(_root, new string('x', 180), new string('y', 80), "Luma.exe");
        var error = Assert.Throws<SystemIntegrationException>(() => Service(longPath).SetAutoStart(true));
        Assert.Equal("INVALID_REQUEST", error.Code);
        Assert.Contains("260", error.Message);
        Assert.True(Service().GetIntegration().AutoStartHere);
    });

    [Fact]
    public void CreatesRealShellShortcutWithSettingsArgumentsAndCanRefreshAfterMoving() => Sta(() =>
    {
        Assert.True(Service().CreateDesktopShortcut().DesktopShortcut);
        InspectLink((dynamic link) =>
        {
            Assert.Equal(Exe, (string)link.TargetPath, ignoreCase: true);
            Assert.Equal("--settings", (string)link.Arguments);
            Assert.Equal(Path.GetDirectoryName(Exe), (string)link.WorkingDirectory, ignoreCase: true);
            Assert.Equal("Luma Quick Launch", (string)link.Description);
        });
        var moved = Path.Combine(_root, "moved", "Luma.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(Exe, moved);
        Assert.False(Service(moved).GetIntegration().DesktopShortcut);
        Assert.True(Service(moved).CreateDesktopShortcut().DesktopShortcut);
        Assert.False(Service().GetIntegration().DesktopShortcut);
        Assert.Single(Directory.GetFiles(Desktop));
    });

    [Fact]
    public void DoesNotOverwriteUnrelatedSameNameShortcut() => Sta(() =>
    {
        InspectLink((dynamic link) =>
        {
            link.TargetPath = Environment.ProcessPath!;
            link.Arguments = "--unrelated";
            link.Description = "Unrelated shortcut";
            link.Save();
        });
        var original = File.ReadAllBytes(Link);
        Assert.False(Service().GetIntegration().DesktopShortcut);
        Assert.Equal("ACCESS_DENIED", Assert.Throws<SystemIntegrationException>(() => Service().CreateDesktopShortcut()).Code);
        Assert.Equal(original, File.ReadAllBytes(Link));
    });

    [Fact]
    public void ValidBridgeCallsReturnActualStateWithoutChangingAppRevision() => Sta(() =>
    {
        var store = new StateStore(Path.Combine(_root, "state"));
        store.Load();
        var router = new BridgeRouter(store, new LaunchService(store, new FakeShell()), new FakePicker(), new FakeWindows(), new ImmediateSync(), integration: Service());
        var client = new FakeClient();
        foreach (var (method, parameters, autoStart, desktop) in new[]
        {
            ("system.getIntegration", "{}", false, false),
            ("system.setAutoStart", "{\"enabled\":true}", true, false),
            ("system.createDesktopShortcut", "{}", true, true),
            ("system.setAutoStart", "{\"enabled\":false}", false, true),
        })
        {
            client.Sent.Clear();
            router.HandleMessage(client, $$"""{"protocol":1,"type":"request","id":"system","method":"{{method}}","params":{{parameters}}}""").GetAwaiter().GetResult();
            using var response = JsonDocument.Parse(Assert.Single(client.Sent));
            var result = response.RootElement.GetProperty("result");
            Assert.Equal(autoStart, result.GetProperty("autoStart").GetBoolean());
            Assert.Equal(autoStart, result.GetProperty("autoStartHere").GetBoolean());
            Assert.Equal(desktop, result.GetProperty("desktopShortcut").GetBoolean());
        }
        Assert.Equal(0, store.Current.Revision);
    });

    private void InspectLink(Action<dynamic> work)
    {
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!);
            shortcut = ((dynamic)shell!).CreateShortcut(Link);
            work(shortcut);
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void Sta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { work(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "STA system integration test timed out.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_registry, false);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
