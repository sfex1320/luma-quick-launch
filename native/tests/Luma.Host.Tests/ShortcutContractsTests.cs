using System.Text.Json;
using System.Text.Json.Nodes;
using Luma.Host.Bridge;
using Xunit;
namespace Luma.Host.Tests;
public sealed class ShortcutContractsTests
{
    private static AppState Read(string bindings)
    {
        var state = JsonSerializer.SerializeToNode(StateValidator.EmptyState(), ContractsJson.Options)!;
        state["preferences"]!["shortcuts"] = JsonNode.Parse(bindings);
        return state.Deserialize<AppState>(ContractsJson.Options)!;
    }
    [Theory]
    [InlineData("KeyA", false, "global", "dock")]
    [InlineData("ControlLeft", true, "panel", "dock")]
    [InlineData("KeyA", true, "global", "openDirectory")]
    [InlineData("KeyA", true, "panel", "project")]
    [InlineData("KeyA", true, "panel", "unknown")]
    [InlineData("KeyK", true, "panel", "dock")]
    public void RejectsUnsafeOrIncompleteBinding(string code, bool ctrl, string scope, string action)
    {
        var state = Read(JsonSerializer.Serialize(new[] { new { id="a", code, ctrl, shift=false, alt=false, scope, action } }));
        Assert.NotEmpty(StateValidator.Validate(state));
    }
    [Theory]
    [InlineData("[{\"id\":\"a\",\"code\":\"Space\",\"ctrl\":true,\"shift\":false,\"alt\":true,\"scope\":\"global\",\"action\":\"dock\"}]")]
    [InlineData("[{\"id\":\"a\",\"code\":\"KeyA\",\"ctrl\":true,\"shift\":false,\"alt\":false,\"scope\":\"panel\",\"action\":\"dock\",\"projectId\":\"p\"}]")]
    [InlineData("[{\"id\":\"a\",\"code\":\"Enter\",\"ctrl\":true,\"shift\":false,\"alt\":false,\"scope\":\"global\",\"action\":\"dock\"},{\"id\":\"b\",\"code\":\"NumpadEnter\",\"ctrl\":true,\"shift\":false,\"alt\":false,\"scope\":\"panel\",\"action\":\"search\"}]")]
    public void RejectsReservedChordExtraneousTargetAndWin32AliasConflict(string bindings) => Assert.NotEmpty(StateValidator.Validate(Read(bindings)));
    [Fact] public void BoundsBindingsAndRejectsUnknownIncomingProperties()
    {
        var state = StateValidator.EmptyState();
        state.Preferences.Shortcuts = Enumerable.Range(0,129).Select(i=>new ShortcutBinding{Id="s"+i,Code="KeyA",Action="dock"}).ToList();
        Assert.NotEmpty(StateValidator.Validate(state));
        Assert.Throws<JsonException>(()=>Read("""[{"id":"a","code":"KeyA","ctrl":true,"shift":false,"alt":false,"scope":"global","action":"dock","path":"C:\\bad"}]"""));
        Assert.Throws<JsonException>(()=>Read("""[{"id":"a","code":"KeyA","scope":"global","action":"dock"}]"""));
    }
    [Fact] public void RejectsDuplicateChordAcrossScopes()
    {
        var state = Read("""[{"id":"a","code":"KeyA","ctrl":true,"shift":false,"alt":false,"scope":"global","action":"dock"},{"id":"b","code":"KeyA","ctrl":true,"shift":false,"alt":false,"scope":"panel","action":"search"}]""");
        Assert.NotEmpty(StateValidator.Validate(state));
    }
    [Fact] public void BindingSurvivesRoundTripAndAllowsMissingSavedTarget()
    {
        var state = Read("""[{"id":"a","code":"KeyA","ctrl":false,"shift":false,"alt":false,"scope":"panel","action":"item","projectId":"deleted","itemId":"gone"}]""");
        Assert.Empty(StateValidator.Validate(state));
        Assert.Contains("shortcuts", JsonSerializer.Serialize(state, ContractsJson.Options));
    }
}
