using System.Windows;
using Luma.Host.Bridge;
using Luma.Host.Services;

namespace Luma.Host.Tests;

/// <summary>测试桩：直接记录 PostJson 输出。</summary>
public sealed class FakeClient : IHostClient
{
    public string ClientId { get; }
    public List<string> Sent { get; } = new();
    public FakeClient(string id = "test") => ClientId = id;
    public void PostJson(string json) => Sent.Add(json);
    public void Detach() { }
}

/// <summary>同步直通调度：测试里立即执行。</summary>
public sealed class ImmediateSync : ISyncContext
{
    public List<Action> Posted { get; } = new();
    public void Post(Action action) { Posted.Add(action); action(); }
    public Task<T> RunBackground<T>(Func<Task<T>> work) => work();
    public Task<T> PostAsync<T>(Func<Task<T>> func) => func();
}

public sealed class FakePicker : IFolderPicker
{
    public string? Result { get; set; }
    public int Calls { get; private set; }
    public Task<string?> PickFolderAsync(IntPtr owner) { Calls++; return Task.FromResult(Result); }
}

public sealed class FakeWindows : IWindowHost
{
    public List<(bool Expanded, Rect[] Rects, bool Interacting, long? VisibilityId)> Syncs { get; } = new();
    public List<string> Settings { get; } = new();
    public int ShowDockCalls;
    public void SyncDock(bool expanded, Rect[] rects, bool interacting = false, long? visibilityId = null) => Syncs.Add((expanded, rects, interacting, visibilityId));
    public void OpenSettings(string section) => Settings.Add(section);
    public IntPtr SettingsOwnerHandle => IntPtr.Zero;
    public void ShowDock() => ShowDockCalls++;
}

public sealed class FakeShell : IShellExecutor
{
    public List<string> Launched { get; } = new();
    public string? FailWith { get; set; }
    public int LaunchCount => Launched.Count;
    public string? TryLaunch(string path)
    {
        if (FailWith is not null) return FailWith;
        Launched.Add(path);
        return null;
    }
}

public sealed class FakeProbe : IFileSystemProbe
{
    public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, bool>? Hook { get; set; }
    public bool Exists(string path) => Hook?.Invoke(path) ?? Existing.Contains(path);
}
