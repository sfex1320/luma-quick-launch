using System.Diagnostics;
using System.IO;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public class ProjectTestTerminalExecutionTests
{
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
