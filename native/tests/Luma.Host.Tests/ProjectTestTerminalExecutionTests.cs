using System.Diagnostics;
using System.IO;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public class ProjectTestTerminalExecutionTests
{
    [Theory]
    [InlineData("development", null)]
    [InlineData("development", "false")]
    [InlineData("development", "true")]
    [InlineData("test", null)]
    [InlineData("test", "false")]
    [InlineData("manual", null)]
    [InlineData("manual", "false")]
    [InlineData("manual", "true")]
    public async Task ActualTerminalPreservesDevelopmentAndManualEnvironmentButKeepsTestsOffline(string branch, string? inheritedOffline)
    {
        var directory = Path.Combine(Path.GetTempPath(), "luma-launch-environment-" + Guid.NewGuid().ToString("N"), "project & ' [literal] $()");
        Directory.CreateDirectory(directory);
        var tool = Path.Combine(directory, "fixture tool.cmd");
        var marker = Path.Combine(directory, "environment.txt");
        var inherited = new Dictionary<string, string>
        {
            ["COREPACK_ENABLE_NETWORK"] = "1", ["COREPACK_ENABLE_AUTO_PIN"] = "1",
            ["GOPROXY"] = "https://proxy.invalid", ["GONOPROXY"] = "*.internal",
            ["GOSUMDB"] = "fixture-sumdb", ["GOVCS"] = "git:all", ["GOTOOLCHAIN"] = "auto",
            ["ORT_LIB_PATH"] = @"C:\fixture ONNX runtime", ["ORT_OFFLINE"] = "true",
        };
        // Harmless local recorder only: no Cargo, npm, project source or network access.
        await File.WriteAllTextAsync(tool, "@echo off\r\n> \"environment.txt\" echo CARGO_NET_OFFLINE=%CARGO_NET_OFFLINE%\r\n" +
            string.Concat(inherited.Keys.Select(key => $">> \"environment.txt\" echo {key}=%{key}%\r\n")) +
            ">> \"environment.txt\" echo ARGS=%*\r\n>> \"environment.txt\" echo CWD=\"%CD%\"\r\nexit /b 0\r\n");
        using var process = new Process();
        var started = false;
        try
        {
            var mode = branch == "development" ? ProjectExecutionMode.Development : ProjectExecutionMode.Test;
            var command = new ProjectTestCommand(directory, "pnpm", ["run", "tauri", "dev"],
                branch == "manual" ? "\"" + tool + "\" run tauri dev" : null, mode);
            var info = WindowsProjectTestTerminal.BuildStartInfo(command, tool);
            if (branch == "manual") info.Arguments = info.Arguments.Replace("/k ", "/c ", StringComparison.Ordinal);
            else info.ArgumentList.Remove("-NoExit");
            info.CreateNoWindow = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            // Set this child's inherited environment without mutating the test runner/user environment.
            if (inheritedOffline is null) info.Environment.Remove("CARGO_NET_OFFLINE");
            else info.Environment["CARGO_NET_OFFLINE"] = inheritedOffline;
            foreach (var (key, value) in inherited) info.Environment[key] = value;
            process.StartInfo = info; started = process.Start(); Assert.True(started);
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var diagnostic = await stdout + await stderr;
            Assert.True(process.ExitCode == 0 && File.Exists(marker), diagnostic);
            var observed = (await File.ReadAllLinesAsync(marker)).Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1]);
            Assert.Equal(branch == "test" ? "true" : inheritedOffline ?? "", observed["CARGO_NET_OFFLINE"]);
            var offline = new Dictionary<string, string>
            {
                ["COREPACK_ENABLE_NETWORK"] = "0", ["COREPACK_ENABLE_AUTO_PIN"] = "0", ["GOPROXY"] = "off",
                ["GONOPROXY"] = "none", ["GOSUMDB"] = "off", ["GOVCS"] = "*:off", ["GOTOOLCHAIN"] = "local",
            };
            foreach (var (key, value) in inherited)
                Assert.Equal(branch == "test" && offline.TryGetValue(key, out var forced) ? forced : value, observed[key]);
            Assert.Equal("run tauri dev", observed["ARGS"]);
            Assert.Equal("\"" + directory + "\"", observed["CWD"], StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (started && !process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
            Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
        }
    }

    [Fact]
    public void ManualCmdRejectsUncWorkingDirectoryInsteadOfSilentlyRunningInWindowsDirectory()
    {
        var error = Assert.Throws<FolderOperationException>(() =>
            WindowsProjectTestTerminal.BuildStartInfo(new(@"\\server\share\project", "cmd", [], "echo fixture"), "ignored.exe"));
        Assert.Equal(ProtocolErrors.InvalidRequest, error.Code);
        Assert.Contains("盘符", error.Message);
    }

    [Theory]
    [InlineData("echo fixture-ok>terminal-marker.txt")]
    [InlineData("echo ignored | findstr ignored >nul && echo fixture-ok>terminal-marker.txt")]
    [InlineData("\"%ComSpec%\" /d /c \"echo fixture-ok>terminal-marker.txt\"")]
    [InlineData("quoted-script")]
    public async Task ManualCmdExecutesSavedBodyInLiteralWorkingDirectory(string savedCommand)
    {
        var directory = Path.Combine(Path.GetTempPath(), "luma-manual-terminal-" + Guid.NewGuid().ToString("N"), "项目 & ' [literal] $() % !");
        Directory.CreateDirectory(directory);
        var tool = Path.Combine(directory, "fixture tool.cmd");
        var marker = Path.Combine(directory, "terminal-marker.txt");
        await File.WriteAllTextAsync(tool, "@echo off\r\n> \"terminal-marker.txt\" echo fixture-ok\r\nexit /b 0\r\n");
        if (savedCommand == "quoted-script") savedCommand = "\"" + tool + "\"";
        using var process = new Process();
        var started = false;
        try
        {
            var info = WindowsProjectTestTerminal.BuildStartInfo(new(directory, "cmd", [], savedCommand), "ignored.exe");
            Assert.Equal(directory, info.WorkingDirectory);
            Assert.EndsWith("cmd.exe", info.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/k ", info.Arguments);
            Assert.False(info.UseShellExecute);
            // Close only this fixture terminal after executing the real production CMD body, with no visible window.
            info.Arguments = info.Arguments.Replace("/k ", "/c ", StringComparison.Ordinal);
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            process.StartInfo = info;
            started = process.Start();
            Assert.True(started);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var output = await stdout;
            var error = await stderr;
            Assert.True(process.ExitCode == 0, output + error);
            Assert.True(File.Exists(marker), output + error);
            Assert.Equal("fixture-ok", (await File.ReadAllTextAsync(marker)).Trim());
        }
        finally
        {
            if (started && !process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
            Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
        }
    }

    [Fact]
    public async Task WindowsPowerShellExecutesCmdFromLiteralUnicodeDirectoryWithoutInterpolation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "luma-terminal-" + Guid.NewGuid().ToString("N"), "项目 & ' [literal] $()");
        Directory.CreateDirectory(directory);
        var tool = Path.Combine(directory, "fixture tool.cmd");
        var marker = Path.Combine(directory, "terminal-marker.txt");
        // A local harmless fixture, not repository code or a downloaded test runner.
        await File.WriteAllTextAsync(tool, "@echo off\r\n> \"terminal-marker.txt\" echo fixture-ok\r\nexit /b 0\r\n");
        using var process = new Process();
        var started = false;
        try
        {
            var info = WindowsProjectTestTerminal.BuildStartInfo(new(directory, "npm", ["run", "test"]), tool);
            // Exercise the actual production encoded body using Windows PowerShell 5.
            // The harness only closes the terminal on completion and prevents a visible window.
            info.ArgumentList.Remove("-NoExit");
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            process.StartInfo = info;
            started = process.Start();
            Assert.True(started);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var output = await stdout;
            var error = await stderr;
            Assert.True(process.ExitCode == 0, output + error);
            Assert.True(File.Exists(marker), output + error);
            Assert.Equal("fixture-ok", (await File.ReadAllTextAsync(marker)).Trim());
        }
        finally
        {
            if (started && !process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
            // Exact per-test GUID directory, no user paths.
            Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
        }
    }
}
