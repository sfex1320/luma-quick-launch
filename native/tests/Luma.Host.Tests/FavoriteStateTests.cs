using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public sealed class FavoriteStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "luma-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FavoriteSurvivesSaveBroadcastReloadAndBackupRecovery(bool favorite)
    {
        var node = JsonSerializer.SerializeToNode(TestStates.OneProject("favorite", "D:\\Example"), ContractsJson.Options)!;
        node["projects"]![0]!["favorite"] = favorite;
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(Repo.ContractsDir(), "state.schema.json")));
        Assert.True(schema.Evaluate(node).IsValid);
        var state = node.Deserialize<AppState>(ContractsJson.Options)!;
        var store = new StateStore(_directory);
        store.Load();
        string? changed = null;
        store.StateChanged += value => changed = JsonSerializer.Serialize(value, ContractsJson.Options);
        Assert.Equal(SaveOutcome.Saved, store.Save(state, 0).Outcome);
        AssertFavorite(changed!, favorite);

        var reloaded = new StateStore(_directory);
        Assert.Equal(StoreOutcome.Loaded, reloaded.Load().Outcome);
        AssertFavorite(JsonSerializer.Serialize(reloaded.Current, ContractsJson.Options), favorite);
        Assert.Equal(SaveOutcome.RevisionConflict, reloaded.Save(state, 0).Outcome);
        Assert.Equal(SaveOutcome.Saved, reloaded.Save(reloaded.Current, 1).Outcome);
        File.WriteAllText(reloaded.MainPath, "broken main");
        var recovery = new StateStore(_directory).Load();
        Assert.Equal(StoreOutcome.LoadedFromBackup, recovery.Outcome);
        AssertFavorite(JsonSerializer.Serialize(recovery.State, ContractsJson.Options), favorite);
    }

    [Fact]
    public void LegacyProjectWithoutFavoriteLoadsAndResavesWithoutInventingTheField()
    {
        Directory.CreateDirectory(_directory);
        var store = new StateStore(_directory);
        File.WriteAllText(store.MainPath, JsonSerializer.Serialize(TestStates.OneProject("legacy", "D:\\Legacy"), ContractsJson.Options));
        Assert.Equal(StoreOutcome.Loaded, store.Load().Outcome);
        Assert.Equal(SaveOutcome.Saved, store.Save(store.Current, 0).Outcome);
        var saved = JsonNode.Parse(File.ReadAllText(store.MainPath))!;
        Assert.False(saved["projects"]![0]!.AsObject().ContainsKey("favorite"));
        Assert.Equal("D:\\Legacy", saved["projects"]![0]!["items"]![0]!["path"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    public void FavoriteRejectsNonBooleanConfiguration(string invalid)
    {
        var node = JsonSerializer.SerializeToNode(TestStates.OneProject("invalid", "D:\\Example"), ContractsJson.Options)!;
        node["projects"]![0]!["favorite"] = JsonNode.Parse(invalid);
        Assert.Throws<JsonException>(() => node.Deserialize<AppState>(ContractsJson.Options));
    }

    private static void AssertFavorite(string json, bool favorite) =>
        Assert.Equal(favorite, JsonNode.Parse(json)!["projects"]![0]!["favorite"]?.GetValue<bool>());

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
