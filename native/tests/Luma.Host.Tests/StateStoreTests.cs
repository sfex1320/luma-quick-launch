using System.IO;
using System.Text.Json;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;
using Path = System.IO.Path;

namespace Luma.Host.Tests;

public class StateStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly StateStore _store;

    public StateStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "luma-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new StateStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void 首次读取_返回空状态_不创建文件()
    {
        var result = _store.Load();
        Assert.Equal(StoreOutcome.EmptyNewInstall, result.Outcome);
        Assert.NotNull(result.State);
        Assert.Equal(0, result.State!.Revision);
        Assert.Empty(result.State.Projects);
        Assert.False(File.Exists(_store.MainPath));
    }

    [Fact]
    public void 成功保存_修订号加一_并写盘可读回()
    {
        _store.Load();
        var state = TestStates.OneProject("atelier", "D:\\Projects\\Atelier");
        state.Revision = 0;
        var saved = _store.Save(state, 0);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.Equal(1, saved.State!.Revision);
        Assert.True(File.Exists(_store.MainPath));

        var reloaded = new StateStore(_dir).Load();
        Assert.Equal(StoreOutcome.Loaded, reloaded.Outcome);
        Assert.Equal(1, reloaded.State!.Revision);
        Assert.Single(reloaded.State.Projects);
    }

    [Fact]
    public void 过期保存_不写盘_主文件保持不变()
    {
        _store.Load();
        var first = TestStates.OneProject("a", "D:\\A");
        first.Revision = 0;
        _store.Save(first, 0);
        var contentAfterFirst = File.ReadAllText(_store.MainPath);

        var second = TestStates.OneProject("b", "D:\\B");
        second.Revision = 0; // 伪造过期 expectedRevision
        var result = _store.Save(second, 0);
        Assert.Equal(SaveOutcome.RevisionConflict, result.Outcome);
        Assert.Equal(contentAfterFirst, File.ReadAllText(_store.MainPath));
        Assert.Equal(1, _store.Current.Revision);
    }

    [Fact]
    public void 保存时state_revision与expectedRevision不一致_拒绝()
    {
        _store.Load();
        var state = TestStates.OneProject("a", "D:\\A");
        state.Revision = 5;
        var result = _store.Save(state, 0);
        Assert.Equal(SaveOutcome.InvalidState, result.Outcome);
    }

    [Fact]
    public void 主文件损坏_从有效备份恢复()
    {
        _store.Load();
        var state = TestStates.OneProject("atelier", "D:\\Projects\\Atelier");
        state.Revision = 0;
        _store.Save(state, 0);
        var state2 = TestStates.OneProject("atelier", "D:\\Projects\\Atelier2");
        state2.Revision = 1;
        _store.Save(state2, 1); // 此时 bak 是 revision 1 的旧版本
        File.WriteAllText(_store.MainPath, "{ this is not valid json !!!");

        var fresh = new StateStore(_dir);
        var result = fresh.Load();
        Assert.Equal(StoreOutcome.LoadedFromBackup, result.Outcome);
        Assert.Equal("D:\\Projects\\Atelier", result.State!.Projects[0].Items[0].Path);
    }

    [Fact]
    public void 两份文件都损坏_返回错误_不静默丢数据()
    {
        File.WriteAllText(_store.MainPath, "broken {{{");
        File.WriteAllText(_store.BackupPath, "also broken {{{");
        var result = _store.Load();
        Assert.Equal(StoreOutcome.Corrupted, result.Outcome);
        Assert.Contains("损坏", result.Message);
    }

    [Fact]
    public void 重复项目ID_拒绝保存()
    {
        _store.Load();
        var state = TestStates.Empty();
        state.Projects.Add(TestStates.Project("dup", "D:\\A"));
        state.Projects.Add(TestStates.Project("dup", "D:\\B"));
        var result = _store.Save(state, 0);
        Assert.Equal(SaveOutcome.InvalidState, result.Outcome);
        Assert.Contains("重复", result.Message);
    }

    [Fact]
    public void 损坏加载后禁止空状态覆盖原文件()
    {
        File.WriteAllText(_store.MainPath, "broken main");
        File.WriteAllText(_store.BackupPath, "broken backup");
        Assert.Equal(StoreOutcome.Corrupted, _store.Load().Outcome);
        Assert.Equal(SaveOutcome.IoError, _store.Save(TestStates.Empty(), 0).Outcome);
        Assert.Equal("broken main", File.ReadAllText(_store.MainPath));
        Assert.Equal("broken backup", File.ReadAllText(_store.BackupPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 备份恢复后首次保存保留有效备份(bool brokenMain)
    {
        var good = JsonSerializer.Serialize(TestStates.OneProject("recover", "D:\\Recover"), ContractsJson.Options);
        File.WriteAllText(_store.BackupPath, good);
        if (brokenMain) File.WriteAllText(_store.MainPath, "broken main");
        Assert.Equal(StoreOutcome.LoadedFromBackup, _store.Load().Outcome);
        Assert.Equal(SaveOutcome.Saved, _store.Save(_store.Current, 0).Outcome);
        Assert.True(File.Exists(_store.BackupPath));
        Assert.Equal(good, File.ReadAllText(_store.BackupPath));
        File.WriteAllText(_store.MainPath, "broken again");
        var reloaded = new StateStore(_dir).Load();
        Assert.Equal(StoreOutcome.LoadedFromBackup, reloaded.Outcome);
        Assert.Equal("recover", reloaded.State!.Projects[0].Id);
    }

    [Fact]
    public void 缺失必要字段的JSON不能伪装成有效空状态()
    {
        File.WriteAllText(_store.MainPath, "{}");
        Assert.Equal(StoreOutcome.Corrupted, _store.Load().Outcome);
    }

    [Fact]
    public void 项目内重复入口ID_拒绝保存()
    {
        _store.Load();
        var state = TestStates.OneProject("atelier", "D:\\A");
        state.Projects[0].Items.Add(TestStates.Item("main", "D:\\B", "folder"));
        var result = _store.Save(state, 0);
        Assert.Equal(SaveOutcome.InvalidState, result.Outcome);
    }

    [Fact]
    public void 保存成功_旧主文件保留为备份()
    {
        _store.Load();
        var v1 = TestStates.OneProject("atelier", "D:\\V1");
        v1.Revision = 0;
        _store.Save(v1, 0);
        var v2 = TestStates.OneProject("atelier", "D:\\V2");
        v2.Revision = 1;
        var saved = _store.Save(v2, 1);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);
        Assert.True(File.Exists(_store.BackupPath));
        var backup = JsonSerializer.Deserialize<AppState>(File.ReadAllText(_store.BackupPath), ContractsJson.Options);
        Assert.Equal("D:\\V1", backup!.Projects[0].Items[0].Path);
    }

    [Fact]
    public void 无主文件但备份存在_从备份恢复()
    {
        _store.Load();
        var v1 = TestStates.OneProject("atelier", "D:\\Only");
        v1.Revision = 0;
        _store.Save(v1, 0);
        var v2 = TestStates.OneProject("atelier", "D:\\Only2");
        v2.Revision = 1;
        _store.Save(v2, 1); // 第二次保存产生 bak（revision 1）
        File.Delete(_store.MainPath);

        var fresh = new StateStore(_dir);
        var result = fresh.Load();
        Assert.Equal(StoreOutcome.LoadedFromBackup, result.Outcome);
        Assert.Equal("D:\\Only", result.State!.Projects[0].Items[0].Path);
    }
}

public static class TestStates
{
    public static AppState Empty() => new()
    {
        SchemaVersion = 1,
        Revision = 0,
        Projects = new List<Project>(),
        Preferences = new Preferences(),
    };

    public static LaunchItem Item(string id, string path, string kind = "folder") => new()
    {
        Id = id,
        Name = id,
        Path = path,
        Kind = kind,
    };

    public static Project Project(string id, string path) => new()
    {
        Id = id,
        Name = id,
        Description = "",
        Color = "mint",
        Pinned = false,
        Items = new List<LaunchItem> { Item("main", path) },
    };

    public static AppState OneProject(string id, string path)
    {
        var state = Empty();
        state.Projects.Add(Project(id, path));
        return state;
    }
}
