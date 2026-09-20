using System.Diagnostics;
using System.IO;
using Luma.Host.Bridge;

namespace Luma.Host.Services;

/// <summary>文件系统探针抽象：测试注入 fake，网络路径用超时包装，避免真实 IO 卡测试线程。</summary>
public interface IFileSystemProbe
{
    bool Exists(string path);
}

public sealed class RealFileSystemProbe : IFileSystemProbe
{
    public static readonly RealFileSystemProbe Instance = new();
    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}

/// <summary>Shell 启动抽象：测试统计副作用次数。生产实现 UseShellExecute，不走 cmd /c 或字符串拼接。</summary>
public interface IShellExecutor
{
    /// <summary>返回 null 表示系统已接受启动；否则为中文错误。</summary>
    string? TryLaunch(string path);
    int LaunchCount { get; }
}

public sealed class RealShellExecutor : IShellExecutor
{
    public static readonly RealShellExecutor Shared = new();
    private static readonly WindowReuseService Reuse = new(new WindowsWindowReusePlatform(LaunchWithShell));
    private static readonly Lazy<System.Collections.Concurrent.BlockingCollection<Action>> Queue = new(() =>
    {
        var queue = new System.Collections.Concurrent.BlockingCollection<Action>(32);
        var worker = new Thread(() => { foreach (var work in queue.GetConsumingEnumerable()) work(); })
            { IsBackground = true, Name = "Luma window reuse" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return queue;
    });
    private static int _launchCount;
    public int LaunchCount => Volatile.Read(ref _launchCount);

    public string? TryLaunch(string path) => TryLaunch(path, CancellationToken.None);

    public string? TryLaunch(string path, CancellationToken callerCancellation)
    {
        // Every caller is a background launch operation. One bounded STA worker owns COM
        // objects; blocked Shell extensions cannot grow workers or stall window.sync.
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        var token = cancellation.Token;
        if (!Queue.Value.TryAdd(() =>
        {
            try { completion.TrySetResult(Reuse.Open(path, token)); }
            catch (OperationCanceledException) { completion.TrySetResult("窗口查找超时，请稍后重试。"); }
            catch (Exception ex) { Log.Error($"窗口复用失败：{ex}"); completion.TrySetResult("无法检查或打开目标窗口，请稍后重试。"); }
            finally { cancellation.Dispose(); }
        })) { cancellation.Dispose(); return "窗口打开请求较多，请稍后重试。"; }
        if (completion.Task.Wait(TimeSpan.FromSeconds(3))) return completion.Task.Result;
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        return "窗口查找仍在进行，请稍后重试。";
    }

    private static string? LaunchWithShell(string path)
    {
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = !WebsiteService.IsUrl(path) && Path.GetDirectoryName(path) is { Length: > 0 } dir ? dir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                // Shell 返回 null 且无异常：系统选择默认处理（如协议由默认应用接管），视为已接受。
                Log.Info($"Shell 已处理启动请求：{path}");
            }
            Interlocked.Increment(ref _launchCount);
            Log.Info($"Shell 新启动：{path}");
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Error($"Shell 启动失败 ({ex.NativeErrorCode}): {path} -> {ex.Message}");
            return ex.NativeErrorCode is 5 or 1200 or 1203 or 1222
                ? "没有权限访问该路径或目标不可用。"
                : "系统无法打开该路径，请检查文件关联或路径是否有效。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or DirectoryNotFoundException or NotSupportedException)
        {
            Log.Error($"Shell 启动失败: {path} -> {ex.Message}");
            return "路径已失效，请重新设置。";
        }
    }
}

public sealed class LaunchOutcome
{
    public bool Accepted { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }

    public static LaunchOutcome Ok() => new() { Accepted = true };
    public static LaunchOutcome Fail(string code, string message) => new() { Accepted = false, ErrorCode = code, Message = message };
}

/// <summary>
/// shell.openItem 实现：按 ID 从最新已保存状态解析路径 → 静态校验 → 后台存在性检查（带超时，网络路径不卡 UI）→ Shell 启动。
/// 同一请求 id 在 60 秒内返回缓存结果，不重复启动。
/// </summary>
public sealed class LaunchService
{
    /// <summary>存在性检查超时：覆盖不可达 UNC；超时按 PATH_NOT_FOUND 处理且不重试。生产默认 5 秒，测试可注入更短值。</summary>
    public TimeSpan ProbeTimeout { get; }
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Dictionary<string, (Lazy<LaunchOutcome> Outcome, DateTime At)> _recent = new();
    private readonly StateStore _store;
    private readonly IShellExecutor _shell;
    private readonly IFileSystemProbe _probe;

    public LaunchService(StateStore store, IShellExecutor? shell = null, IFileSystemProbe? probe = null, TimeSpan? probeTimeout = null)
    {
        _store = store;
        _shell = shell ?? RealShellExecutor.Shared;
        _probe = probe ?? RealFileSystemProbe.Instance;
        ProbeTimeout = probeTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>调用方（BridgeRouter）负责把此方法放进后台任务，避免网络路径卡 UI。</summary>
    public LaunchOutcome OpenItem(string requestId, string projectId, string itemId)
    {
        Lazy<LaunchOutcome> operation;
        lock (_gate)
        {
            if (_recent.TryGetValue(requestId, out var cached) &&
                (!cached.Outcome.IsValueCreated || DateTime.UtcNow - cached.At < DedupWindow))
                operation = cached.Outcome;
            else
            {
                operation = new Lazy<LaunchOutcome>(() => ResolveAndLaunch(projectId, itemId),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _recent[requestId] = (operation, DateTime.UtcNow);
                if (_recent.Count > 256) CleanupDedup();
            }
        }
        // 相同 ID 共享进行中的操作；不同 ID 不在全局锁内等待网络探针。
        return operation.Value;
    }

    private LaunchOutcome ResolveAndLaunch(string projectId, string itemId)
    {
        if (_store.LoadError is { } errorMessage) return LaunchOutcome.Fail("IO_ERROR", errorMessage);
        var state = _store.Current;
        var project = state.Projects.FirstOrDefault(p => p.Id == projectId);
        if (project is null) return LaunchOutcome.Fail("PATH_NOT_FOUND", "未找到对应项目，可能已被删除。");
        var item = project.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) return LaunchOutcome.Fail("PATH_NOT_FOUND", "未找到对应入口，可能已被删除。");

        var invalid = item.Kind == "url" ? WebsiteService.IsUrl(item.Path) ? null : "网址无效。" : PathRules.Validate(item.Path);
        if (invalid is not null) return LaunchOutcome.Fail("INVALID_REQUEST", invalid);

        if (item.Kind != "url" && !ProbeWithTimeout(item.Path))
            return LaunchOutcome.Fail("PATH_NOT_FOUND", "文件夹或文件已移动、不可达，请重新设置路径。");

        var error = _shell.TryLaunch(item.Path);
        if (error is not null) return LaunchOutcome.Fail("ACCESS_DENIED", error);
        Log.Info($"shell.openItem 启动 project={projectId} item={itemId} path={item.Path}");
        return LaunchOutcome.Ok();
    }

    private bool ProbeWithTimeout(string path)
    {
        try
        {
            var task = Task.Run(() => _probe.Exists(path));
            return task.Wait(ProbeTimeout) && task.Result;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    private void CleanupDedup()
    {
        var cutoff = DateTime.UtcNow - DedupWindow;
        foreach (var key in _recent.Where(kv => kv.Value.Outcome.IsValueCreated && kv.Value.At < cutoff).Select(kv => kv.Key).ToList())
            _recent.Remove(key);
    }
}
