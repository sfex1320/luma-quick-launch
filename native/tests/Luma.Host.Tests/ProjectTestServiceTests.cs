using System.IO;
using System.Text;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

[Collection("Folder IO")]
public sealed class ProjectTestServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luma-project-tests", Guid.NewGuid().ToString("N"), "项目 ' & $() 空格");
    private readonly StateStore _store;
    private readonly FolderService _folders;
    private readonly RecordingTerminal _terminal = new();
    private readonly ProjectTestService _tests;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public ProjectTestServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StateStore(Path.Combine(_dir, "config"));
        _store.Load(); _store.Save(TestStates.OneProject("p", _dir), 0);
        _folders = new(_store, new FakeShell(), clock: () => _now);
        _tests = new(_folders, _terminal, clock: () => _now);
    }
    public void Dispose() { Directory.Delete(Path.GetDirectoryName(_dir)!, true); }
    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);
    private void Package(string script = "vitest run") => Write("package.json", System.Text.Json.JsonSerializer.Serialize(new { scripts = new { test = script } }));
    private async Task<(string Folder, ProjectTestTask? Task)> Detect()
    {
        var folder = (await _folders.ListAsync("dock", "p", "main")).FolderId;
        return (folder, await _tests.DetectAsync("dock", "p", "main", folder));
    }
    private Task<bool> Run(string id) => _tests.RunAsync("dock", "p", "main", id);
    private static async Task Error(string code, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<FolderOperationException>(action);
        Assert.Equal(code, error.Code); Assert.NotEmpty(error.Message);
    }

    [Theory]
    [InlineData("package-lock.json", "npm")]
    [InlineData("npm-shrinkwrap.json", "npm")]
    [InlineData("pnpm-lock.yaml", "pnpm")]
    [InlineData("yarn.lock", "yarn")]
    public async Task LockFileSelectsRunnerAndDetectionNeverExecutesScript(string file, string manager)
    {
        Package("echo danger & $(whoami); ' injected"); Write(file, "lock");
        var first = await Detect();
        Assert.Equal(manager + " run test", first.Task!.Command);
        Assert.Empty(_terminal.Commands);
        Assert.Equal(first.Task, (await Detect()).Task);
        Assert.True(await Run(first.Task.Id));
        var launched = Assert.Single(_terminal.Commands);
        Assert.Equal(manager, launched.Tool);
        Assert.Equal(new[] { "run", "test" }, launched.Arguments);
        Assert.Equal(_dir, launched.Directory);
    }
    [Fact]
    public async Task NoTestScriptAndAmbiguousManagersOrProjectTypesAreSkipped()
    {
        Write("package.json", "{\"scripts\":{\"start\":\"node app.js\"}}");
        Assert.Null((await Detect()).Task);
        Package(); Write("package-lock.json", "npm"); Write("yarn.lock", "yarn");
        Assert.Null((await Detect()).Task);
        File.Delete(Path.Combine(_dir, "yarn.lock")); Write("go.mod", "module example.com/test\n");
        Assert.Null((await Detect()).Task);
        Assert.Empty(_terminal.Commands);
    }
    [Theory]
    [InlineData("pnpm@10.0.0", "pnpm run test")]
    [InlineData("yarn@4.0.0", "yarn run test")]
    [InlineData("unknown@1", null)]
    public async Task ExplicitPackageManagerMustBeSupportedAndAgreeWithLock(string manager, string? expected)
    {
        Write("package.json", System.Text.Json.JsonSerializer.Serialize(new { packageManager = manager, scripts = new { test = "vitest" } }));
        Assert.Equal(expected, (await Detect()).Task?.Command);
        Write("package-lock.json", "npm");
        Assert.Null((await Detect()).Task);
    }
    [Theory]
    [InlineData("Cargo.toml", "[package]\nname = 'example'", "cargo test --offline")]
    [InlineData("go.mod", "module example.com/test\ngo 1.20", "go test ./...")]
    [InlineData("Tests.csproj", "<Project><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>", "dotnet test \"Tests.csproj\" --no-restore")]
    public async Task ExplicitProjectMarkersUseFixedNoRestoreCommands(string manifest, string body, string expected)
    {
        Write(manifest, body);
        Assert.Equal(expected, (await Detect()).Task?.Command);
        Assert.Empty(_terminal.Commands);
    }
    [Fact]
    public async Task DotnetDoesNotGuessTestProjectOrEvaluateConditionalMsbuild()
    {
        Write("App.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Assert.Null((await Detect()).Task);
        Write("App.csproj", "<Project><PropertyGroup Condition=\"'$(X)' == 'yes'\"><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        Assert.Null((await Detect()).Task);
        Assert.Empty(_terminal.Commands);
    }
    [Fact]
    public async Task ManifestOrLockContentChangeInvalidatesIssuedTask()
    {
        Package(); Write("package-lock.json", "first");
        var first = (await Detect()).Task!;
        Package("different test script");
        await Error("INVALID_REQUEST", () => Run(first.Id));
        var second = (await Detect()).Task!;
        Write("package-lock.json", "other");
        await Error("INVALID_REQUEST", () => Run(second.Id));
        Assert.NotEqual(first.Id, second.Id);
        Assert.Empty(_terminal.Commands);
    }
    [Fact]
    public async Task TokenCannotCrossClientRootOrExpireAndDetachInvalidatesIt()
    {
        Package(); var task = (await Detect()).Task!;
        await Error("INVALID_REQUEST", () => _tests.RunAsync("settings", "p", "main", task.Id));
        await Error("INVALID_REQUEST", () => _tests.RunAsync("dock", "other", "main", task.Id));
        await Error("INVALID_REQUEST", () => Run(_dir));
        _now = _now.AddMinutes(2);
        await Error("INVALID_REQUEST", () => Run(task.Id));
        task = (await Detect()).Task!;
        _tests.Detach("dock");
        await Error("INVALID_REQUEST", () => Run(task.Id));
        task = (await Detect()).Task!;
        var state = _store.Current;
        Directory.CreateDirectory(Path.Combine(_dir, "child"));
        state.Projects[0].Items[0].Path = Path.Combine(_dir, "child");
        _store.Save(state, state.Revision);
        await Error("INVALID_REQUEST", () => Run(task.Id));
        Assert.Empty(_terminal.Commands);
    }
    [Fact]
    public async Task DeletedRootOrDetachedFolderTokenCannotLaunch()
    {
        Package(); var task = (await Detect()).Task!;
        _folders.Detach("dock");
        await Error("INVALID_REQUEST", () => Run(task.Id));
        task = (await Detect()).Task!;
        var state = _store.Current; state.Projects.Clear(); _store.Save(state, state.Revision);
        await Error("PATH_NOT_FOUND", () => Run(task.Id));
        Assert.Empty(_terminal.Commands);
    }
    [Fact]
    public async Task DuplicateClicksAndFreshTokensDoNotOpenAnotherActiveTerminal()
    {
        Package(); var task = (await Detect()).Task!;
        Assert.True(await Run(task.Id));
        await Error("BUSY", () => Run(task.Id));
        Assert.Single(_terminal.Commands);
        Package("updated"); var next = (await Detect()).Task!;
        await Error("BUSY", () => Run(next.Id));
        _terminal.Processes[0].Exited = true;
        Assert.True(await Run(next.Id));
        Assert.Equal(2, _terminal.Commands.Count);
    }
    [Fact]
    public async Task NestedDirectoryIsDetectedOnlyWhenItsIssuedFolderIsRequested()
    {
        var nested = Path.Combine(_dir, "nested"); Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "package.json"), "{\"scripts\":{\"test\":\"vitest\"}}");
        Assert.Null((await Detect()).Task);
        var list = await _folders.ListAsync("dock", "p", "main");
        var id = list.Entries.Single(e => e.Name == "nested").Id;
        var task = await _tests.DetectAsync("dock", "p", "main", id);
        Assert.True(await Run(task!.Id));
        Assert.Equal(nested, Assert.Single(_terminal.Commands).Directory);
    }
    [Fact]
    public async Task OversizeManifestAndDtdAreRejectedWithoutExecution()
    {
        Write("package.json", new string(' ', 262145));
        await Error("INVALID_REQUEST", async () => { await Detect(); });
        File.Delete(Path.Combine(_dir, "package.json"));
        Write("Tests.csproj", "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><Project>&e;</Project>");
        await Error("INVALID_REQUEST", async () => { await Detect(); });
        Assert.Empty(_terminal.Commands);
    }
    [Fact]
    public async Task TimeoutKeepsWorkerSlotsAndCannotIssueOrLaunchLateTask()
    {
        using var unblock = new ManualResetEventSlim(); using var started = new CountdownEvent(2); using var ended = new CountdownEvent(2);
        var files = new BlockingFiles(unblock, started, ended);
        var service = new ProjectTestService(_folders, _terminal, files, timeout: TimeSpan.FromMilliseconds(100));
        var folder = (await _folders.ListAsync("dock", "p", "main")).FolderId;
        var a = service.DetectAsync("dock", "p", "main", folder);
        var b = service.DetectAsync("dock", "p", "main", folder);
        Assert.True(started.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            await Error("BUSY", () => a); await Error("BUSY", () => b);
            await Error("BUSY", () => service.DetectAsync("dock", "p", "main", folder));
        }
        finally { unblock.Set(); Assert.True(ended.Wait(TimeSpan.FromSeconds(3))); }
        Assert.Empty(_terminal.Commands);
    }
    [Fact]
    public void TerminalUsesEncodedLiteralArgumentsAndKeepsOutputVisible()
    {
        var command = new ProjectTestCommand(_dir, "dotnet", ["test", ".\\Test '& $().csproj", "--no-restore"]);
        var info = WindowsProjectTestTerminal.BuildStartInfo(command, "C:\\tools ' &\\dotnet.exe");
        Assert.Equal(_dir, info.WorkingDirectory);
        Assert.False(info.UseShellExecute); Assert.False(info.CreateNoWindow);
        Assert.Contains("-NoProfile", info.ArgumentList); Assert.Contains("-NoExit", info.ArgumentList);
        var body = Encoding.Unicode.GetString(Convert.FromBase64String(info.ArgumentList.Last()));
        Assert.Contains("Set-Location -LiteralPath '" + _dir.Replace("'", "''") + "'", body);
        Assert.Contains("'.\\Test ''& $().csproj'", body);
        Assert.Contains("$env:COREPACK_ENABLE_NETWORK='0'", body);
        Assert.Contains("$env:GOPROXY='off'", body);
        Assert.Contains("$env:GONOPROXY='none'", body);
        Assert.Contains("$env:GOVCS='*:off'", body);
        Assert.Contains("$env:GOSUMDB='off'", body);
        Assert.Contains("$env:GOTOOLCHAIN='local'", body);
        Assert.Contains("@testArgs", body);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowTerminalProbeDoesNotBlockDetachAndCannotLaunchAfterCancellation(bool detach)
    {
        Package();
        using var unblock = new ManualResetEventSlim(); using var started = new ManualResetEventSlim(); using var ended = new ManualResetEventSlim();
        var terminal = new BlockingTerminal(unblock, started, ended);
        var tests = new ProjectTestService(_folders, terminal, timeout: TimeSpan.FromMilliseconds(150));
        var folder = (await _folders.ListAsync("dock", "p", "main")).FolderId;
        var task = (await tests.DetectAsync("dock", "p", "main", folder))!;
        var run = tests.RunAsync("dock", "p", "main", task.Id);
        Assert.True(started.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            if (detach)
            {
                await Task.Run(() => tests.Detach("dock")).WaitAsync(TimeSpan.FromSeconds(1));
                unblock.Set();
                await Error("CANCELLED", () => run);
            }
            else await Error("BUSY", () => run);
        }
        finally { unblock.Set(); Assert.True(ended.Wait(TimeSpan.FromSeconds(3))); }
        Assert.Equal(0, terminal.Launched);
    }
    private sealed class BlockingTerminal(ManualResetEventSlim unblock, ManualResetEventSlim started, ManualResetEventSlim ended) : IProjectTestTerminal
    {
        public int Launched;
        public IProjectTestProcess Start(ProjectTestCommand command, CancellationToken cancellation)
        {
            started.Set();
            try { unblock.Wait(); cancellation.ThrowIfCancellationRequested(); Launched++; return new FakeProcess(); }
            finally { ended.Set(); }
        }
    }
    private sealed class RecordingTerminal : IProjectTestTerminal
    {
        public List<ProjectTestCommand> Commands { get; } = new();
        public List<FakeProcess> Processes { get; } = new();
        public IProjectTestProcess Start(ProjectTestCommand command, CancellationToken cancellation)
        { Commands.Add(command); var process = new FakeProcess(); Processes.Add(process); return process; }
    }
    private sealed class FakeProcess : IProjectTestProcess
    { public bool Exited; public bool HasExited => Exited; public void Dispose() { } }
    private sealed class BlockingFiles(ManualResetEventSlim unblock, CountdownEvent started, CountdownEvent ended) : IProjectTestFiles
    {
        public byte[]? Read(string directory, string name, int limit)
        { started.Signal(); try { unblock.Wait(); return null; } finally { ended.Signal(); } }
        public string[] ProjectNames(string directory) => [];
    }
}
