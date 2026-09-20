using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public sealed class WindowsRecentProjectSourceTests
{
    [Fact]
    public async Task RealShellLinkMetadataIsReadWithoutLaunchingAndArgumentLinksAreSkipped()
    {
        var directory = Path.Combine(Path.GetTempPath(), "luma-recent-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await Sta(() =>
            {
                var software = Path.Combine(directory, "Photoshop.exe");
                var document = Path.Combine(directory, "项目.psd");
                File.WriteAllText(software, "fixture, never executed"); File.WriteAllText(document, "fixture");
                var appLink = Path.Combine(directory, "软件.lnk");
                var recent = Path.Combine(directory, "recent"); Directory.CreateDirectory(recent);
                Link(appLink, software, ""); Link(Path.Combine(recent, "项目.lnk"), document, "");
                Link(Path.Combine(recent, "带参数.lnk"), software, document);
                var source = new WindowsRecentProjectSource(recent);
                Assert.Equal(software, source.ResolveExecutable(appLink, CancellationToken.None), StringComparer.OrdinalIgnoreCase);
                Assert.Equal(document, Assert.Single(source.ReadRecent(CancellationToken.None)).Path, StringComparer.OrdinalIgnoreCase);
                Link(appLink, software, "--unsafe-argument");
                Assert.Null(source.ResolveExecutable(appLink, CancellationToken.None));
                Assert.False(source.IsRegularFile(recent));
                Assert.False(source.IsRegularFile(document + ":stream"));
            });
        }
        finally { Directory.Delete(directory, true); }
    }
    private static Task Sta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        worker.SetApartmentState(ApartmentState.STA); worker.IsBackground = true; worker.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static void Link(string path, string target, string arguments)
    {
        object? shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!);
            link = shell!.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
            link!.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, [target]);
            link.GetType().InvokeMember("Arguments", BindingFlags.SetProperty, null, link, [arguments]);
            link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}
