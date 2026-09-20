using System.Text.Json;
using System.Windows;
using Luma.Host.Services;

namespace Luma.Host.Bridge;

/// <summary>挂接在宿主上的一个前端页面（浮岛 / 管理窗）。PostJson 由窗口实现，内部回到 UI 线程调用 PostWebMessageAsJson。</summary>
public interface IHostClient
{
    string ClientId { get; }
    void PostJson(string json);
    void Detach();
}

/// <summary>线程调度抽象：生产用 WPF Dispatcher；测试用同步直通。</summary>
public interface ISyncContext
{
    void Post(Action action);
    Task<T> RunBackground<T>(Func<Task<T>> work);
    Task<T> PostAsync<T>(Func<Task<T>> func);
}

public sealed class DispatcherSyncContext : ISyncContext
{
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    public DispatcherSyncContext(System.Windows.Threading.Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Post(Action action) => _dispatcher.BeginInvoke(action);

    public Task<T> RunBackground<T>(Func<Task<T>> work) => Task.Run(work);

    public Task<T> PostAsync<T>(Func<Task<T>> func)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.BeginInvoke(async () =>
        {
            try { completion.SetResult(await func()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        return completion.Task;
    }
}

/// <summary>宿主窗口能力（由 App 实现）：window.sync / window.openSettings / 对话框归属。</summary>
public interface IWindowHost
{
    void SyncDock(bool expanded, Rect[] rects, bool interacting = false, long? visibilityId = null);
    void OpenSettings(string section);
    IntPtr SettingsOwnerHandle { get; }
    void ShowDock();
    void CloseSearch() { }
}

/// <summary>系统文件夹选择对话框抽象；生产实现为 IFileOpenDialog，测试可注入 fake。</summary>
public interface IFolderPicker
{
    /// <summary>返回所选文件夹路径；用户取消返回 null。</summary>
    Task<string?> PickFolderAsync(IntPtr owner);
}

public static class ProtocolErrors
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string UnsupportedProtocol = "UNSUPPORTED_PROTOCOL";
    public const string MethodNotFound = "METHOD_NOT_FOUND";
    public const string RevisionConflict = "REVISION_CONFLICT";
    public const string PathNotFound = "PATH_NOT_FOUND";
    public const string AccessDenied = "ACCESS_DENIED";
    public const string IoError = "IO_ERROR";
    public const string Cancelled = "CANCELLED";
    public const string Busy = "BUSY";
    public const string InternalError = "INTERNAL_ERROR";

    public static string MessageFor(string code) => code switch
    {
        UnsupportedProtocol => "协议版本不受支持。",
        MethodNotFound => "内核不支持该方法。",
        RevisionConflict => "配置已在其他窗口更新，请重新载入后再保存。",
        PathNotFound => "文件夹或文件已移动，请重新设置路径。",
        AccessDenied => "没有权限访问该路径。",
        IoError => "配置读写失败，请检查磁盘或稍后重试。",
        Cancelled => "操作已取消。",
        Busy => "内核忙，请稍后重试。",
        InternalError => "内核内部错误。",
        _ => "请求无效。",
    };
}

/// <summary>
/// 协议 v1 路由：解析 WebMessageReceived 的 JSON 请求，分发到 StateStore / LaunchService / FolderPicker / WindowHost，
/// 恰好回一个 response；保存成功后向除来源外的其他页面广播 app.stateChanged。
/// </summary>
public sealed class BridgeRouter
{
    private readonly object _gate = new();
    private readonly List<IHostClient> _clients = new();
    private readonly StateStore _store;
    private readonly LaunchService _launcher;
    private readonly IFolderPicker _folderPicker;
    private readonly IWindowHost _windows;
    private readonly ISyncContext _sync;
    private readonly SearchService _search;
    private readonly FolderService _folders;
    private readonly ShellIconService _icons;
    private readonly ShortcutImportService _imports = new();

    public BridgeRouter(StateStore store, LaunchService launcher, IFolderPicker folderPicker, IWindowHost windows, ISyncContext sync, FolderService? folders = null, ShellIconService? icons = null)
    {
        _store = store;
        _launcher = launcher;
        _folderPicker = folderPicker;
        _windows = windows;
        _sync = sync;
        _search = new SearchService(store);
        _folders = folders ?? new FolderService(store);
        _icons = icons ?? new ShellIconService(store);
    }

    public IReadOnlyList<IHostClient> Clients
    {
        get { lock (_gate) return _clients.ToList(); }
    }

    public void Attach(IHostClient client)
    {
        lock (_gate) _clients.Add(client);
        Log.Info($"桥接客户端接入：{client.ClientId}（当前 {Clients.Count} 个）");
    }

    public void Detach(IHostClient client)
    {
        lock (_gate) _clients.Remove(client);
        _folders.Detach(client.ClientId);
        client.Detach();
        Log.Info($"桥接客户端断开：{client.ClientId}（剩余 {Clients.Count} 个）");
    }

    /// <summary>处理一条来自 source 的原始消息。异常兜底为 INTERNAL_ERROR，保证每个请求恰好一个应答。</summary>
    public async Task HandleMessage(IHostClient source, string raw, IReadOnlyList<string>? droppedPaths = null)
    {
        string id = "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("protocol", out var protocolEl) || protocolEl.ValueKind != JsonValueKind.Number || protocolEl.GetInt32() != 1)
            {
                // protocol 缺失/错误只能从消息里尽力取 id 回应；取不到用空 id。
                if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String) id = idEl.GetString() ?? "";
                RespondError(source, id, ProtocolErrors.UnsupportedProtocol);
                return;
            }
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "request")
            {
                if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String) id = idEl.GetString() ?? "";
                RespondError(source, id, ProtocolErrors.InvalidRequest);
                return;
            }
            if (!root.TryGetProperty("id", out var requestIdEl) || requestIdEl.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
            {
                RespondError(source, id, ProtocolErrors.InvalidRequest);
                return;
            }
            id = requestIdEl.GetString() ?? "";
            var method = methodEl.GetString() ?? "";
            var hasParams = root.TryGetProperty("params", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object;
            var parameters = hasParams ? paramsEl : default;

            switch (method)
            {
                case "app.getState":
                    if (_store.LoadError is { } loadError) RespondError(source, id, ProtocolErrors.IoError, loadError);
                    else RespondOk(source, id, _store.Current);
                    return;
                case "app.saveState":
                    await HandleSaveState(source, id, parameters);
                    return;
                case "shell.openItem":
                    await HandleOpenItem(source, id, parameters);
                    return;
                case "shell.getIcon":
                    await HandleGetIcon(source, id, parameters);
                    return;
                case "folder.list":
                case "folder.open":
                    await HandleFolder(source, id, method, parameters);
                    return;
                case "shell.pickFolder":
                    await HandlePickFolder(source, id);
                    return;
                case "shell.pickFiles":
                    var files = await _sync.PostAsync(() =>
                    {
                        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, DereferenceLinks = false, CheckFileExists = true, Title = "添加软件或文件", Filter = "软件和文件|*.*" };
                        var owner = _windows.SettingsOwnerHandle;
                        var ownerWindow = System.Windows.Interop.HwndSource.FromHwnd(owner)?.RootVisual as Window;
                        var selected = ownerWindow is null ? dialog.ShowDialog() : dialog.ShowDialog(ownerWindow);
                        return Task.FromResult(selected == true ? dialog.FileNames : Array.Empty<string>());
                    });
                    RespondOk(source, id, await _sync.RunBackground(() => Task.FromResult(_imports.Resolve(files))));
                    return;
                case "shell.resolveDrop":
                    if (droppedPaths is null || droppedPaths.Count == 0 || droppedPaths.Count > 100)
                        RespondError(source, id, ProtocolErrors.InvalidRequest, "请从资源管理器拖入真实的文件夹、软件或文件（最多 100 项）。");
                    else RespondOk(source, id, await _sync.RunBackground(() => Task.FromResult(_imports.Resolve(droppedPaths))));
                    return;
                case "search.query":
                    if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("query", out var queryEl) || queryEl.ValueKind != JsonValueKind.String || !parameters.TryGetProperty("scope", out var scopeEl) || scopeEl.ValueKind != JsonValueKind.String || queryEl.GetString()!.Length > 200 || scopeEl.GetString() is not ("all" or "shortcuts" or "files" or "content" or "settings"))
                        RespondError(source, id, ProtocolErrors.InvalidRequest);
                    else RespondOk(source, id, await _search.QueryAsync(source.ClientId, queryEl.GetString()!, scopeEl.GetString()!));
                    return;
                case "search.open":
                    if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("resultId", out var resultEl) || resultEl.ValueKind != JsonValueKind.String)
                    { RespondError(source, id, ProtocolErrors.InvalidRequest); return; }
                    var outcome = await _sync.RunBackground(() => Task.FromResult(_search.Open(source.ClientId, id, resultEl.GetString()!)));
                    if (outcome.Accepted) RespondOk(source, id, new { accepted = true });
                    else RespondError(source, id, outcome.ErrorCode ?? ProtocolErrors.InternalError, outcome.Message);
                    return;
                case "window.closeSearch":
                    _sync.Post(_windows.CloseSearch);
                    RespondOk(source, id, new { accepted = true });
                    return;
                case "window.sync":
                    HandleWindowSync(source, id, parameters);
                    return;
                case "window.openSettings":
                    HandleOpenSettings(source, id, parameters);
                    return;
                default:
                    RespondError(source, id, ProtocolErrors.MethodNotFound);
                    return;
            }
        }
        catch (FolderOperationException ex)
        {
            RespondError(source, id, ex.Code, ex.Message);
        }
        catch (JsonException)
        {
            RespondError(source, id, ProtocolErrors.InvalidRequest);
        }
        catch (ArgumentException ex)
        {
            RespondError(source, id, ProtocolErrors.InvalidRequest, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error($"处理消息异常 method 路径 id={id}: {ex}");
            RespondError(source, id, ProtocolErrors.InternalError);
        }
    }

    private async Task HandleGetIcon(IHostClient source, string id, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("projectId", out var project) || project.ValueKind != JsonValueKind.String ||
            !parameters.TryGetProperty("itemId", out var item) || item.ValueKind != JsonValueKind.String ||
            parameters.EnumerateObject().Any(p => p.Name is not ("projectId" or "itemId" or "size")))
        { RespondError(source, id, ProtocolErrors.InvalidRequest); return; }
        var size = 64;
        if (parameters.TryGetProperty("size", out var value) && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out size) || size is not (32 or 48 or 64 or 96)))
        { RespondError(source, id, ProtocolErrors.InvalidRequest); return; }
        RespondOk(source, id, new { dataUrl = await _icons.GetAsync(project.GetString()!, item.GetString()!, size) });
    }

    private async Task HandleFolder(IHostClient source, string id, string method, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("projectId", out var project) || project.ValueKind != JsonValueKind.String ||
            !parameters.TryGetProperty("itemId", out var item) || item.ValueKind != JsonValueKind.String ||
            parameters.EnumerateObject().Any(p => p.Name is not ("projectId" or "itemId") && p.Name != (method == "folder.list" ? "folderId" : "entryId")))
        { RespondError(source, id, ProtocolErrors.InvalidRequest); return; }
        if (method == "folder.list")
        {
            string? folderId = null;
            if (parameters.TryGetProperty("folderId", out var folder))
            {
                if (folder.ValueKind != JsonValueKind.String) { RespondError(source, id, ProtocolErrors.InvalidRequest); return; }
                folderId = folder.GetString();
            }
            RespondOk(source, id, await _folders.ListAsync(source.ClientId, project.GetString()!, item.GetString()!, folderId));
        }
        else
        {
            if (!parameters.TryGetProperty("entryId", out var entry) || entry.ValueKind != JsonValueKind.String)
            { RespondError(source, id, ProtocolErrors.InvalidRequest); return; }
            var outcome = await _folders.OpenAsync(source.ClientId, id, project.GetString()!, item.GetString()!, entry.GetString()!);
            if (outcome.Accepted) { Log.Info($"folder.open 启动 project={project.GetString()} item={item.GetString()}"); RespondOk(source, id, new { accepted = true }); }
            else RespondError(source, id, outcome.ErrorCode ?? ProtocolErrors.InternalError, outcome.Message);
        }
    }

    private async Task HandleSaveState(IHostClient source, string id, JsonElement parameters)
    {
        long expectedRevision;
        AppState incoming;
        try
        {
            if (parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty("state", out var stateEl) || stateEl.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty("expectedRevision", out var revEl) || revEl.ValueKind != JsonValueKind.Number)
            {
                RespondError(source, id, ProtocolErrors.InvalidRequest);
                return;
            }
            expectedRevision = revEl.GetInt64();
            incoming = stateEl.Deserialize<AppState>(ContractsJson.Options)
                       ?? throw new JsonException("state 反序列化为空");
        }
        catch (JsonException)
        {
            RespondError(source, id, ProtocolErrors.InvalidRequest);
            return;
        }

        var result = await _sync.RunBackground(async () => _store.Save(incoming, expectedRevision));        switch (result.Outcome)
        {
            case SaveOutcome.Saved:
                RespondOk(source, id, result.State!);
                BroadcastStateChanged(result.State!, except: source);
                return;
            case SaveOutcome.RevisionConflict:
                RespondError(source, id, ProtocolErrors.RevisionConflict);
                return;
            case SaveOutcome.InvalidState:
                RespondError(source, id, ProtocolErrors.InvalidRequest, result.Message);
                return;
            default:
                RespondError(source, id, ProtocolErrors.IoError, result.Message);
                return;
        }
    }

    private async Task HandleOpenItem(IHostClient source, string id, JsonElement parameters)
    {
        string? projectId = null, itemId = null, requestId = id;
        if (parameters.ValueKind == JsonValueKind.Object)
        {
            if (parameters.TryGetProperty("projectId", out var p) && p.ValueKind == JsonValueKind.String) projectId = p.GetString();
            if (parameters.TryGetProperty("itemId", out var i) && i.ValueKind == JsonValueKind.String) itemId = i.GetString();
        }
        if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(itemId))
        {
            RespondError(source, id, ProtocolErrors.InvalidRequest, "projectId 与 itemId 不能为空。");
            return;
        }
        // 网络路径探针可能耗时 5 秒，必须在后台执行，不阻塞 UI。
        var outcome = await _sync.RunBackground(async () => _launcher.OpenItem(requestId, projectId!, itemId!));
        if (outcome.Accepted) RespondOk(source, id, new { accepted = true });
        else RespondError(source, id, outcome.ErrorCode ?? ProtocolErrors.InternalError, outcome.Message);
    }

    private async Task HandlePickFolder(IHostClient source, string id)
    {
        var owner = _windows.SettingsOwnerHandle;
        // 对话框必须运行在 STA UI 线程；模态期间 Dispatcher 继续泵消息，浮岛与其他请求不受影响。
        var path = await _sync.PostAsync(() => _folderPicker.PickFolderAsync(owner));
        if (path is null)
        {
            RespondOk(source, id, null);
            return;
        }
        var name = FolderDisplayName(path);
        RespondOk(source, id, new { path, name });
    }

    public static string FolderDisplayName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private void HandleWindowSync(IHostClient source, string id, JsonElement parameters)
    {
        try
        {
            if (parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty("expanded", out var expandedEl) || expandedEl.ValueKind != JsonValueKind.True && expandedEl.ValueKind != JsonValueKind.False ||
                !parameters.TryGetProperty("rects", out var rectsEl) || rectsEl.ValueKind != JsonValueKind.Array)
            {
                RespondError(source, id, ProtocolErrors.InvalidRequest);
                return;
            }
            var expanded = expandedEl.GetBoolean();
            long? visibilityId = null;
            if (parameters.TryGetProperty("visibilityId", out var visibilityEl))
            {
                if (visibilityEl.ValueKind != JsonValueKind.Number || !visibilityEl.TryGetDouble(out var value) ||
                    !double.IsFinite(value) || value < 0 || value > 9007199254740991L || value != Math.Truncate(value))
                { RespondError(source, id, ProtocolErrors.InvalidRequest); return; }
                visibilityId = (long)value;
            }
            var interacting = false;
            if (parameters.TryGetProperty("interacting", out var interactingEl))
            {
                if (interactingEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                { RespondError(source, id, ProtocolErrors.InvalidRequest); return; }
                interacting = interactingEl.GetBoolean();
            }
            var rects = new List<Rect>();
            foreach (var rectEl in rectsEl.EnumerateArray())
            {
                if (rectEl.ValueKind != JsonValueKind.Object) continue;
                if (!rectEl.TryGetProperty("x", out var x) || !rectEl.TryGetProperty("y", out var y) ||
                    !rectEl.TryGetProperty("width", out var w) || !rectEl.TryGetProperty("height", out var h)) continue;
                if (x.ValueKind != JsonValueKind.Number || y.ValueKind != JsonValueKind.Number ||
                    w.ValueKind != JsonValueKind.Number || h.ValueKind != JsonValueKind.Number) continue;
                rects.Add(new Rect(x.GetDouble(), y.GetDouble(), w.GetDouble(), h.GetDouble()));
            }
            if (rects.Count == 0 && expanded)
            {
                RespondError(source, id, ProtocolErrors.InvalidRequest, "expanded=true 时 rects 不能为空。");
                return;
            }
            var rectsCopy = rects.ToArray();
            _sync.Post(() =>
            {
                try
                {
                    _windows.SyncDock(expanded, rectsCopy, interacting, visibilityId);
                    RespondOk(source, id, new { applied = true });
                }
                catch (Exception ex)
                {
                    Log.Error($"window.sync 应用失败: {ex.Message}");
                    RespondError(source, id, ProtocolErrors.InternalError);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error($"window.sync 处理失败: {ex.Message}");
            RespondError(source, id, ProtocolErrors.InternalError);
        }
    }

    private void HandleOpenSettings(IHostClient source, string id, JsonElement parameters)
    {
        var section = "projects";
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("section", out var sectionEl) && sectionEl.ValueKind == JsonValueKind.String)
        {
            var value = sectionEl.GetString();
            if (value is "projects" or "appearance" or "search") section = value;
            else
            {
                RespondError(source, id, ProtocolErrors.InvalidRequest, "section 仅支持 projects / appearance / search。");
                return;
            }
        }
        var sectionCopy = section;
        _sync.Post(() => _windows.OpenSettings(sectionCopy));
        RespondOk(source, id, new { accepted = true });
    }

    /// <summary>保存成功后向除来源外的所有页面广播 app.stateChanged（来源页面已通过 response 收到结果）。</summary>
    private void BroadcastStateChanged(AppState state, IHostClient except)
    {
        var payload = SerializeEvent("app.stateChanged", state);
        foreach (var client in Clients)
        {
            if (ReferenceEquals(client, except)) continue;
            client.PostJson(payload);
        }
    }

    public void BroadcastVisibility(bool visible, long? visibilityId = null)
    {
        var payload = SerializeEvent("window.visibility", visibilityId is { } id ? (object)new { visible, visibilityId = id } : new { visible });
        foreach (var client in Clients) client.PostJson(payload);
    }

    private static string SerializeEvent(string eventName, object data)
    {
        var payload = new { protocol = 1, type = "event", @event = eventName, data };
        return JsonSerializer.Serialize(payload, ContractsJson.Options);
    }

    private static void RespondOk(IHostClient source, string id, object? result)
    {
        var payload = new { protocol = 1, type = "response", id, ok = true, result };
        source.PostJson(JsonSerializer.Serialize(payload, ContractsJson.Options));
    }

    private static void RespondError(IHostClient source, string id, string code, string? message = null)
    {
        var payload = new
        {
            protocol = 1,
            type = "response",
            id,
            ok = false,
            error = new { code, message = message ?? ProtocolErrors.MessageFor(code) },
        };
        source.PostJson(JsonSerializer.Serialize(payload, ContractsJson.Options));
    }
}
