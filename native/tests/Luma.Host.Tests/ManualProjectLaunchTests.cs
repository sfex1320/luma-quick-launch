using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

[Collection("Folder IO")]
public sealed class ManualProjectLaunchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "luma-manual-" + Guid.NewGuid().ToString("N"));
    private readonly StateStore _store;
    private readonly FolderService _folders;
    private readonly Terminal _terminal = new();
    private readonly ProjectTestService _service;

    public ManualProjectLaunchTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new(Path.Combine(_directory, "state"));
        _store.Load();
        _store.Save(TestStates.OneProject("p", _directory), 0);
        _folders = new(_store, new FakeShell());
        _service = new(_folders, _terminal);
    }
    public void Dispose() => Directory.Delete(_directory, true);
    private AppState Config(string? command, string? workingDirectory = null, string kind = "folder")
    {
        var state = JsonSerializer.SerializeToNode(_store.Current, ContractsJson.Options)!;
        var item = state["projects"]![0]!["items"]![0]!;
        item["kind"] = kind;
        if (command is null) item.AsObject().Remove("launch");
        else item["launch"] = new JsonObject { ["command"] = command, ["workingDirectory"] = workingDirectory ?? _directory };
        return state.Deserialize<AppState>(ContractsJson.Options)!;
    }
    private void Save(string? command, string? workingDirectory = null)
    {
        var state = Config(command, workingDirectory);
        Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
    }
    private async Task<ProjectTestTask?> Detect(string? folder = null)
    {
        folder ??= (await _folders.ListAsync("dock", "p", "main")).FolderId;
        return await _service.DetectAsync("dock", "p", "main", folder);
    }

    [Fact]
    public async Task SavedManualCommandWorksWithoutManifestAndDetectionDoesNotExecute()
    {
        Save("echo manual && echo ready");
        var task = await Detect();
        Assert.NotNull(task);
        Assert.Equal("手动启动", task.Label);
        Assert.Equal("echo manual && echo ready", task.Command);
        Assert.Empty(_terminal.Commands);
        var stored = JsonNode.Parse(File.ReadAllText(_store.MainPath));
        Assert.Equal("echo manual && echo ready", stored!["projects"]![0]!["items"]![0]!["launch"]!["command"]!.GetValue<string>());
        Assert.True(await _service.RunAsync("dock", "p", "main", task.Id));
        Assert.Equal(_directory, Assert.Single(_terminal.Commands).Directory);
    }

    [Fact]
    public async Task RootOverrideSkipsMalformedManifestWhileChildKeepsAutomaticDetection()
    {
        Save("echo manual");
        File.WriteAllText(Path.Combine(_directory, "package.json"), "malformed json");
        Assert.Equal("echo manual", (await Detect())!.Command);
        var nested = Path.Combine(_directory, "child");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "package.json"), "{\"scripts\":{\"test\":\"vitest\"}}");
        var listing = await _folders.ListAsync("dock", "p", "main");
        var task = await Detect(listing.Entries.Single(e => e.Name == "child").Id);
        Assert.Equal("npm run test", task!.Command);
        Assert.True(await _service.RunAsync("dock", "p", "main", task.Id));
        Assert.Equal(nested, Assert.Single(_terminal.Commands).Directory);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("directory")]
    [InlineData("remove")]
    [InlineData("rebind")]
    public async Task ChangedConfigurationCannotRunPreviouslyIssuedTask(string change)
    {
        Save("echo manual");
        var task = (await Detect())!;
        Assert.NotNull(task);
        var child = Path.Combine(_directory, "child");
        Directory.CreateDirectory(child);
        if (change == "command") Save("echo changed");
        if (change == "directory") Save("echo manual", child);
        if (change == "remove") Save(null);
        if (change == "rebind")
        {
            var state = Config("echo manual");
            state.Projects[0].Items[0].Path = child;
            Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
        }
        var error = await Assert.ThrowsAsync<FolderOperationException>(() => _service.RunAsync("dock", "p", "main", task.Id));
        Assert.Equal(ProtocolErrors.InvalidRequest, error.Code);
        Assert.Empty(_terminal.Commands);
    }

    [Fact]
    public async Task RemovingOverrideRestoresAutomaticTestAndOmitsNullFromStoredState()
    {
        Save("echo manual");
        File.WriteAllText(Path.Combine(_directory, "package.json"), "{\"scripts\":{\"test\":\"vitest\"}}");
        Assert.Equal("echo manual", (await Detect())!.Command);
        Save(null);
        Assert.Equal("npm run test", (await Detect())!.Command);
        var stored = JsonNode.Parse(File.ReadAllText(_store.MainPath));
        Assert.False(stored!["projects"]![0]!["items"]![0]!.AsObject().ContainsKey("launch"));
        Assert.Empty(_terminal.Commands);
    }

    [Fact]
    public async Task ManualLaunchUsesSavedAlternateWorkingDirectoryAndDeduplicatesActiveTerminal()
    {
        var alternate = Path.Combine(_directory, "actual workspace");
        Directory.CreateDirectory(alternate);
        Save("echo manual", alternate);
        var task = (await Detect())!;
        Assert.True(await _service.RunAsync("dock", "p", "main", task.Id));
        var launched = Assert.Single(_terminal.Commands);
        Assert.Equal(alternate, launched.Directory);
        Assert.Equal("echo manual", launched.ManualCommand);
        Assert.Empty(launched.Arguments);
        var error = await Assert.ThrowsAsync<FolderOperationException>(() => _service.RunAsync("dock", "p", "main", task.Id));
        Assert.Equal(ProtocolErrors.Busy, error.Code);
        Assert.Single(_terminal.Commands);
    }

    [Fact]
    public async Task SavingOverrideInvalidatesPreviouslyDetectedAutomaticTask()
    {
        File.WriteAllText(Path.Combine(_directory, "package.json"), "{\"scripts\":{\"test\":\"vitest\"}}");
        var automatic = (await Detect())!;
        Save("echo manual");
        var error = await Assert.ThrowsAsync<FolderOperationException>(() => _service.RunAsync("dock", "p", "main", automatic.Id));
        Assert.Equal(ProtocolErrors.InvalidRequest, error.Code);
        Assert.Empty(_terminal.Commands);
    }

    [Theory]
    [InlineData("", "C:\\project", "folder")]
    [InlineData("   ", "C:\\project", "folder")]
    [InlineData("echo one\necho two", "C:\\project", "folder")]
    [InlineData("echo one\recho two", "C:\\project", "folder")]
    [InlineData("echo\0bad", "C:\\project", "folder")]
    [InlineData("echo ok", "relative", "folder")]
    [InlineData("echo ok", "C:relative", "folder")]
    [InlineData("echo ok", "C:\\project\0bad", "folder")]
    [InlineData("echo ok", "C:\\project", "file")]
    [InlineData("echo ok", "C:\\project", "app")]
    public void InvalidLaunchConfigurationIsRejectedBeforeSaving(string command, string cwd, string kind)
    {
        var state = Config(command, cwd, kind);
        Assert.Equal(SaveOutcome.InvalidState, _store.Save(state, state.Revision).Outcome);
    }

    [Fact]
    public void LaunchFieldLengthsAreBounded()
    {
        Assert.NotEmpty(StateValidator.Validate(Config(new string('x', 1001))));
        Assert.NotEmpty(StateValidator.Validate(Config("echo ok", "C:\\" + new string('x', 4094))));
        Assert.Empty(StateValidator.Validate(Config(new string('x', 1000), "C:\\" + new string('x', 4093))));
    }

    private sealed class Terminal : IProjectTestTerminal
    {
        public List<ProjectTestCommand> Commands { get; } = [];
        public IProjectTestProcess Start(ProjectTestCommand command, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); Commands.Add(command); return new Running(); }
    }
    private sealed class Running : IProjectTestProcess
    { public bool HasExited => false; public void Dispose() { } }
}
