using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Luma.Host.Bridge;
using Xunit;
using Path = System.IO.Path;

namespace Luma.Host.Tests;

/// <summary>定位仓库根目录（测试 bin 在 native/tests/.../bin 下，向上找 docs/contracts）。</summary>
public static class Repo
{
    public static string ContractsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "contracts")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("未找到仓库 docs/contracts 目录");
        return Path.Combine(dir.FullName, "docs", "contracts");
    }
}

public class ContractsTests
{
    private static readonly Lazy<JsonSchema> StateSchema = new(() =>
        JsonSchema.FromText(File.ReadAllText(Path.Combine(Repo.ContractsDir(), "state.schema.json"))));

    [Fact]
    public void 空状态_与empty_state_json_逐字段一致()
    {
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo.ContractsDir(), "empty-state.json")));
        var actual = JsonSerializer.SerializeToNode(StateValidator.EmptyState(), ContractsJson.Options);
        Assert.True(JsonNode.DeepEquals(expected, actual),
            $"EmptyState 与 empty-state.json 不一致\n预期: {expected?.ToJsonString()}\n实际: {actual?.ToJsonString()}");
    }

    [Fact]
    public void 空状态_符合state_schema()
    {
        var json = JsonSerializer.Serialize(StateValidator.EmptyState(), ContractsJson.Options);
        var result = StateSchema.Value.Evaluate(JsonNode.Parse(json), new EvaluationOptions { OutputFormat = OutputFormat.Flag });
        Assert.True(result.IsValid, "EmptyState 序列化结果必须符合 state.schema.json");
    }

    [Fact]
    public void 合法完整状态_符合schema且校验通过()
    {
        var state = TestStates.OneProject("atelier", "D:\\Projects\\Atelier");
        state.Projects[0].Description = "设计工作区";
        state.Projects[0].Color = "violet";
        state.Projects[0].Pinned = true;
        state.Projects[0].Items.Add(TestStates.Item("assets", "D:\\Projects\\Atelier\\素材 库"));
        state.Preferences.Width = 1120;
        state.Preferences.Height = 64;
        state.Preferences.IconSize = 24;
        state.Preferences.Radius = 32;
        state.Preferences.Material = "solid";
        state.Preferences.Theme = "dark";
        state.Preferences.ReducedMotion = true;

        Assert.Empty(StateValidator.Validate(state));
        var json = JsonSerializer.Serialize(state, ContractsJson.Options);
        var result = StateSchema.Value.Evaluate(JsonNode.Parse(json), new EvaluationOptions { OutputFormat = OutputFormat.Flag });
        Assert.True(result.IsValid);
    }

    [Fact]
    public void 超出范围的外观_被业务校验拒绝()
    {
        var state = TestStates.Empty();
        state.Preferences.Width = 319;
        Assert.Contains(StateValidator.Validate(state), e => e.Contains("宽度"));
        state.Preferences.Width = 1121;
        Assert.Contains(StateValidator.Validate(state), e => e.Contains("宽度"));
        state.Preferences.Width = 640;
        state.Preferences.Material = "glass";
        Assert.Contains(StateValidator.Validate(state), e => e.Contains("材质"));
    }

    [Fact]
    public void 项目名与描述边界_按schema约束()
    {
        var state = TestStates.Empty();
        state.Projects.Add(TestStates.Project("a", "D:\\A"));
        state.Projects[0].Name = new string('项', 60);
        state.Projects[0].Description = new string('描', 160);
        Assert.Empty(StateValidator.Validate(state));

        state.Projects[0].Name = new string('项', 61);
        Assert.Contains(StateValidator.Validate(state), e => e.Contains("名称"));
        state.Projects[0].Name = "a";
        state.Projects[0].Description = new string('描', 161);
        Assert.Contains(StateValidator.Validate(state), e => e.Contains("描述"));
    }

    [Fact]
    public void 额外字段序列化_被schema拒绝_证明DTO无多余属性()
    {
        // 人为加入额外字段模拟 DTO 与 schema 脱节的情况，schema 的 additionalProperties:false 应拒绝。
        var json = JsonSerializer.Serialize(TestStates.OneProject("a", "D:\\A"), ContractsJson.Options);
        var node = JsonNode.Parse(json)!.AsObject();
        node["extraField"] = "x";
        var result = StateSchema.Value.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.Flag });
        Assert.False(result.IsValid);
    }

    [Fact]
    public void 错误信息_全部为中文且无环境泄露()
    {
        var state = TestStates.Empty();
        state.Preferences.Width = 0;
        var errors = StateValidator.Validate(state);
        Assert.NotEmpty(errors);
        foreach (var error in errors)
        {
            Assert.False(error.Contains("G:\\") || error.Contains("C:\\Users") || error.Contains("Exception"),
                $"错误信息不应包含环境细节：{error}");
        }
    }
}
