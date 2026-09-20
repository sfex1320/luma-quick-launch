using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Luma.Host.Bridge;
using Microsoft.Win32;

namespace Luma.Host.Services;

public sealed record SystemIntegrationState(bool AutoStart, bool AutoStartHere, bool DesktopShortcut);

public sealed class SystemIntegrationException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// User-owned system entries. Call on a single STA (the bridge uses its UI dispatcher).
/// Optional native constructor paths isolate HKCU/desktop IO in tests; never supplied by WebView.
/// Construction and status reads do not create or enable entries.
/// </summary>
public sealed class SystemIntegrationService
{
    public const string RunValueName = "LumaQuickLaunch";
    public const string ShortcutName = "Luma Quick Launch.lnk";
    public const string ShortcutDescription = "Luma Quick Launch";
    public const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultStartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private readonly string _executablePath;
    private readonly string _runKeyPath;
    private readonly string _desktopDirectory;
    private readonly string _startupApprovedKeyPath;

    public SystemIntegrationService(string? executablePath = null, string? runKeyPath = null,
        string? desktopDirectory = null, string? startupApprovedKeyPath = null)
    {
        _executablePath = Path.GetFullPath(executablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前程序。"));
        _runKeyPath = runKeyPath ?? DefaultRunKeyPath;
        _desktopDirectory = desktopDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        _startupApprovedKeyPath = startupApprovedKeyPath ?? DefaultStartupApprovedKeyPath;
    }

    public SystemIntegrationState GetIntegration() => Execute(ReadState);
    public SystemIntegrationState SetAutoStart(bool enabled) => Execute(() =>
    {
        if (enabled)
        {
            var command = $"\"{_executablePath}\" --startup";
            if (command.Length > 260)
                throw new SystemIntegrationException(ProtocolErrors.InvalidRequest, "当前程序路径过长，开机启动命令超过 Windows 的 260 字符限制。请将便携目录移到更短的路径后重试。");
            if (!File.Exists(_executablePath))
                throw new SystemIntegrationException(ProtocolErrors.PathNotFound, "当前程序已移动，请从新位置启动 Luma 后重试。");
            using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath, true);
            key.SetValue(RunValueName, command, RegistryValueKind.String);
            // Only explicit enabling clears a Windows startup-disabled marker.
            using var approved = Registry.CurrentUser.OpenSubKey(_startupApprovedKeyPath, true);
            approved?.DeleteValue(RunValueName, false);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, true);
            if (IsCurrentCommand(key?.GetValue(RunValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)))
            {
                key!.DeleteValue(RunValueName, false);
                using var approved = Registry.CurrentUser.OpenSubKey(_startupApprovedKeyPath, true);
                approved?.DeleteValue(RunValueName, false);
            }
        }
        return ReadState();
    });

    public SystemIntegrationState CreateDesktopShortcut() => Execute(() =>
    {
        RequireSta();
        if (!File.Exists(_executablePath))
            throw new SystemIntegrationException(ProtocolErrors.PathNotFound, "当前程序已移动，请从新位置启动 Luma 后重试。");
        if (string.IsNullOrEmpty(_desktopDirectory) || !Directory.Exists(_desktopDirectory))
            throw new SystemIntegrationException(ProtocolErrors.PathNotFound, "无法找到当前用户桌面目录。");
        var path = Path.Combine(_desktopDirectory, ShortcutName);
        if (File.Exists(path) && !IsOwnedShortcut(path))
            throw new SystemIntegrationException(ProtocolErrors.AccessDenied, "桌面已有同名且不属于 Luma 的快捷方式，请先重命名该快捷方式后重试。");
        object? shell = null, shortcut = null;
        // Save separately so a COM failure cannot destroy an existing shortcut.
        var temporary = Path.Combine(_desktopDirectory, $".luma-{Guid.NewGuid():N}.lnk");
        try
        {
            shell = CreateShell();
            shortcut = ((dynamic)shell).CreateShortcut(temporary);
            dynamic link = shortcut;
            link.TargetPath = _executablePath;
            link.Arguments = "--settings";
            link.WorkingDirectory = Path.GetDirectoryName(_executablePath)!;
            link.IconLocation = _executablePath + ",0";
            link.Description = ShortcutDescription;
            link.Save();
            // Recheck ownership before replacing in case the desktop changed meanwhile.
            if (File.Exists(path))
            {
                if (!IsOwnedShortcut(path)) throw new SystemIntegrationException(ProtocolErrors.AccessDenied, "桌面同名快捷方式已更改，请检查后重试。");
                File.Move(temporary, path, true);
            }
            else File.Move(temporary, path);
        }
        finally
        {
            Release(shortcut); Release(shell);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return ReadState();
    });

    private SystemIntegrationState ReadState()
    {
        using var run = Registry.CurrentUser.OpenSubKey(_runKeyPath);
        var command = run?.GetValue(RunValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        using var approved = Registry.CurrentUser.OpenSubKey(_startupApprovedKeyPath);
        var marker = approved?.GetValue(RunValueName);
        var allowed = marker is null || marker is byte[] { Length: >= 4 } bytes && (BitConverter.ToUInt32(bytes, 0) is 2 or 6);
        var active = command is string { Length: > 0 } && allowed;
        var shortcut = Path.Combine(_desktopDirectory, ShortcutName);
        return new(active, active && IsCurrentCommand(command), File.Exists(shortcut) && ShortcutMatchesCurrent(shortcut));
    }

    private bool IsCurrentCommand(object? value) => value is string command &&
        string.Equals(command.Trim(), $"\"{_executablePath}\" --startup", StringComparison.OrdinalIgnoreCase);

    private bool ShortcutMatchesCurrent(string path) => InspectShortcut(path, (target, arguments, _) =>
        SamePath(target, _executablePath) && arguments == "--settings");

    private bool IsOwnedShortcut(string path) => InspectShortcut(path, (target, arguments, description) =>
        arguments == "--settings" && (SamePath(target, _executablePath) ||
        description == ShortcutDescription && Path.GetFileName(target).Equals("Luma.exe", StringComparison.OrdinalIgnoreCase)));

    private static bool SamePath(string a, string b) => Path.IsPathFullyQualified(a) &&
        string.Equals(Path.GetFullPath(a), b, StringComparison.OrdinalIgnoreCase);

    private static bool InspectShortcut(string path, Func<string, string, string, bool> inspect)
    {
        RequireSta();
        object? shell = null, shortcut = null;
        try
        {
            shell = CreateShell();
            shortcut = ((dynamic)shell).CreateShortcut(path);
            dynamic link = shortcut;
            return inspect((string)link.TargetPath, (string)link.Arguments, (string)link.Description);
        }
        catch (COMException) { return false; }
        finally { Release(shortcut); Release(shell); }
    }

    private static object CreateShell() => Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
    private static void RequireSta()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("系统快捷方式操作必须在 STA 线程执行。");
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    private static SystemIntegrationState Execute(Func<SystemIntegrationState> action)
    {
        try { return action(); }
        catch (SystemIntegrationException) { throw; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        { throw new SystemIntegrationException(ProtocolErrors.AccessDenied, "系统入口操作被拒绝，请检查当前用户的桌面和启动项权限。", ex); }
        catch (Exception ex) when (ex is IOException or COMException)
        { throw new SystemIntegrationException(ProtocolErrors.IoError, "系统入口读写失败，请检查桌面目录和当前程序位置后重试。", ex); }
    }
}
