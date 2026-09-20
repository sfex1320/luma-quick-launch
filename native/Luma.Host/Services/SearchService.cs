using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Luma.Host.Services;

public sealed record SearchResult(string Id, string Title, string Subtitle, string Kind, string Source);
public sealed record SearchResponse(IReadOnlyList<SearchResult> Results, bool IndexAvailable, string Note);
public sealed record IndexedSearchItem(string Title, string Path, string Kind);

public interface IWindowsSearchProvider
{
    Task<IReadOnlyList<IndexedSearchItem>> QueryAsync(string query, string scope, CancellationToken cancellationToken);
}

/// <summary>One read-only, bounded query of the existing Windows index; never crawls the disk.</summary>
public sealed class WindowsSearchProvider : IWindowsSearchProvider
{
    // Even separate consumers cannot accumulate abandoned COM calls when a provider hangs.
    private static readonly SemaphoreSlim Worker = new(1, 1);

    public async Task<IReadOnlyList<IndexedSearchItem>> QueryAsync(string query, string scope, CancellationToken cancellationToken)
    {
        var sql = BuildSql(query, scope);
        if (!await Worker.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("索引查询仍在处理中。");
        try { return await Task.Run(() => Execute(sql, cancellationToken), CancellationToken.None); }
        finally { Worker.Release(); }
    }

    public static string BuildSql(string query, string scope)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200 || query.Any(char.IsControl))
            throw new ArgumentException("搜索内容需为 1–200 个可见字符。", nameof(query));
        if (scope is not ("all" or "files" or "content")) throw new ArgumentException("索引搜索范围无效。", nameof(scope));
        var literal = query.Replace("'", "''", StringComparison.Ordinal);
        var pattern = new StringBuilder();
        foreach (var c in literal)
            pattern.Append(c switch { '%' => "[%]", '_' => "[_]", '[' => "[[]", ']' => "[]]", _ => c.ToString() });
        // A leading-wildcard LIKE walks property values and can time out on large indexes.
        // Only Unicode letters/digits enter the owned CONTAINS grammar; user operators,
        // quotes and wildcards can never become query syntax. Match each filename token prefix.
        var words = Regex.Matches(query, @"[\p{L}\p{N}]+").Select(m => m.Value).ToArray();
        var filename = words.Length > 0
            ? $"CONTAINS(System.FileName, '{string.Join(" AND ", words.Select(word => $"\"{word}*\""))}')"
            : $"System.FileName LIKE '%{pattern}%'";
        // FREETEXT treats the complete SQL string value as natural language, not a CONTAINS expression.
        var body = $"FREETEXT(System.Search.Contents, '{literal}')";
        var predicate = scope == "content" ? body : scope == "files" ? filename : $"({filename} OR {body})";
        return $"SELECT TOP 60 System.ItemNameDisplay, System.ItemPathDisplay, System.FileAttributes FROM SYSTEMINDEX WHERE SCOPE='file:' AND {predicate}";
    }

    private static IReadOnlyList<IndexedSearchItem> Execute(string sql, CancellationToken cancellationToken)
    {
        object? connection = null; object? recordset = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = Type.GetTypeFromProgID("ADODB.Connection") ?? throw new InvalidOperationException("Windows Search 不可用。");
            connection = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows Search 不可用。");
            dynamic db = connection;
            db.ConnectionTimeout = 2;
            db.CommandTimeout = 3;
            db.Mode = 1; // adModeRead
            db.Open("Provider=Search.CollatorDSO.1;Extended Properties='Application=Windows';", "", "", 0);
            cancellationToken.ThrowIfCancellationRequested();
            object affected;
            recordset = db.Execute(sql, out affected, 1); // adCmdText
            dynamic rows = recordset;
            var results = new List<IndexedSearchItem>();
            while (!rows.EOF && results.Count < 60)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var title = ReadField(recordset, 0)?.ToString() ?? "";
                var path = ReadField(recordset, 1)?.ToString() ?? "";
                var attributes = ReadField(recordset, 2);
                var folder = attributes is not null && (Convert.ToInt64(attributes) & 16) != 0;
                if (PathRules.Validate(path) is null)
                    results.Add(new IndexedSearchItem(string.IsNullOrWhiteSpace(title) ? Path.GetFileName(path) : title, path, ShortcutImportService.Classify(path, folder)));
                rows.MoveNext();
            }
            return results;
        }
        finally
        {
            CloseAndRelease(recordset);
            CloseAndRelease(connection);
        }
    }

    private static object? ReadField(object recordset, int index)
    {
        object? fields = null; object? field = null;
        try
        {
            fields = ((dynamic)recordset).Fields;
            field = ((dynamic)fields).Item(index);
            object value = ((dynamic)field).Value;
            return value is DBNull ? null : value;
        }
        finally
        {
            if (field is not null && Marshal.IsComObject(field)) Marshal.FinalReleaseComObject(field);
            if (fields is not null && Marshal.IsComObject(fields)) Marshal.FinalReleaseComObject(fields);
        }
    }

    private static void CloseAndRelease(object? value)
    {
        if (value is null) return;
        try { ((dynamic)value).Close(); } catch { }
        if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}

public sealed class SearchService
{
    private static readonly IReadOnlyDictionary<string, string[]> AppAliasIdentities =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["AI"] = ["Illustrator"],
            ["PS"] = ["Photoshop"],
            ["AE"] = ["After Effects", "AfterEffects"],
            ["PR"] = ["Premiere Pro", "Premiere"],
            ["ID"] = ["InDesign"],
            ["CDR"] = ["CorelDRAW", "Corel Draw"],
        };
    private sealed record Capability(string ClientId, DateTimeOffset Expires, string? ProjectId, string? ItemId, string? Path, string? Setting);
    private sealed record Setting(string Title, string Keywords, string Uri);
    private static readonly Setting[] Settings =
    {
        new("显示设置", "屏幕 显示器 分辨率 缩放 display monitor", "ms-settings:display"),
        new("声音设置", "音量 扬声器 麦克风 声音 sound audio", "ms-settings:sound"),
        new("蓝牙设备", "蓝牙 设备 bluetooth", "ms-settings:bluetooth"),
        new("网络和 Internet", "网络 wifi 无线 以太网 internet network", "ms-settings:network-status"),
        new("个性化", "主题 壁纸 背景 颜色 personalization wallpaper", "ms-settings:personalization"),
        new("已安装的应用", "程序 软件 应用 卸载 apps", "ms-settings:appsfeatures"),
        new("默认应用", "默认 关联 打开方式 default apps", "ms-settings:defaultapps"),
        new("Windows 更新", "更新 补丁 update", "ms-settings:windowsupdate"),
        new("存储设置", "磁盘 空间 存储 storage", "ms-settings:storagesense"),
        new("电源与睡眠", "电源 电池 睡眠 power battery", "ms-settings:powersleep"),
        new("日期和时间", "日期 时间 时区 date time", "ms-settings:dateandtime"),
        new("语言和区域", "语言 输入法 区域 language region", "ms-settings:regionlanguage"),
        new("辅助功能", "辅助 无障碍 accessibility", "ms-settings:easeofaccess"),
        new("隐私设置", "隐私 权限 privacy", "ms-settings:privacy"),
        new("搜索与索引", "搜索 索引 文件 内容 search index", "ms-settings:search-windows")
    };
    private readonly object _gate = new();
    private readonly Dictionary<string, Capability> _capabilities = new();
    private readonly Dictionary<(string Client, string Request), (Lazy<LaunchOutcome> Work, DateTimeOffset At)> _requests = new();
    private sealed class PendingQuery
    {
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Granted { get; set; }
    }
    private readonly Dictionary<string, PendingQuery> _pendingQueries = new();
    private bool _queryActive;
    private readonly SemaphoreSlim _probes = new(1, 1);
    private readonly StateStore _store;
    private readonly IWindowsSearchProvider _provider;
    private readonly IShellExecutor _shell;
    private readonly IFileSystemProbe _probe;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _queryTimeout;
    private readonly LaunchService _launch;

    public SearchService(StateStore store, IWindowsSearchProvider? provider = null, IShellExecutor? shell = null,
        IFileSystemProbe? probe = null, Func<DateTimeOffset>? clock = null, TimeSpan? queryTimeout = null)
    {
        _store = store; _provider = provider ?? new WindowsSearchProvider();
        _shell = shell ?? RealShellExecutor.Shared; _probe = probe ?? RealFileSystemProbe.Instance;
        _clock = clock ?? (() => DateTimeOffset.UtcNow); _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(4);
        _launch = new LaunchService(store, _shell, _probe);
    }

    public async Task<SearchResponse> QueryAsync(string clientId, string query, string scope, bool appAliases = true, bool fuzzyNames = false)
    {
        if (string.IsNullOrEmpty(clientId) || clientId.Length > 200 || query is null || query.Length > 200 || query.Any(char.IsControl)
            || scope is not ("all" or "shortcuts" or "files" or "content" or "settings"))
            throw new ArgumentException("搜索参数无效。");
        query = query.Trim();
        if (query.Length == 0 || scope is "shortcuts" or "settings")
        {
            lock (_gate)
                if (_pendingQueries.Remove(clientId, out var obsolete)) obsolete.Ready.TrySetResult(false);
        }
        var results = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string title, string subtitle, string kind, string source, string? project = null, string? item = null, string? path = null, string? setting = null)
        {
            if (results.Count >= 60 || !seen.Add(setting ?? path ?? $"{project}/{item}")) return;
            var id = Guid.NewGuid().ToString("N");
            lock (_gate)
            {
                foreach (var expired in _capabilities.Where(p => p.Value.Expires <= _clock()).Select(p => p.Key).ToArray()) _capabilities.Remove(expired);
                while (_capabilities.Count >= 1024) _capabilities.Remove(_capabilities.First().Key);
                _capabilities[id] = new Capability(clientId, _clock().AddMinutes(5), project, item, path, setting);
            }
            results.Add(new SearchResult(id, title, subtitle, kind, source));
        }
        bool Matches(string text) => query.Length == 0 || text.Contains(query, StringComparison.OrdinalIgnoreCase);
        if (scope is "all" or "shortcuts")
            foreach (var project in _store.Current.Projects)
                foreach (var item in project.Items)
                    if (Matches($"{project.Name} {item.Name} {item.Path}") ||
                        (appAliases && item.Kind == "app" && MatchesAppAlias(query, item.Name + " " + item.Path)) ||
                        (fuzzyNames && (item.Kind is "folder" or "file") && MatchesSavedNameFuzzy(query, item.Name + " " + Path.GetFileNameWithoutExtension(item.Path))))
                        Add(item.Name, $"{project.Name} · {item.Path}", item.Kind, "shortcut", project.Id, item.Id, item.Path);
        if (scope is "all" or "settings")
            foreach (var setting in Settings)
                if (Matches(setting.Title + " " + setting.Keywords)) Add(setting.Title, "Windows 设置", "setting", "settings", setting: setting.Uri);

        var available = false;
        var note = "仅搜索 Windows 已索引的位置；文件名按词首匹配，正文取决于文件类型和索引设置。";
        if (scope is "all" or "files" or "content")
        {
            if (query.Length == 0) note = "输入关键词以查询 Windows 索引；仅覆盖已索引的位置和支持的文件正文。";
            else if (!await WaitForQueryTurnAsync(clientId)) note = "索引查询已被更新或等待超时；已保存入口与设置仍可使用。";
            else
            {
                using var cancellation = new CancellationTokenSource();
                var token = cancellation.Token;
                // Release only when the real provider finishes, never when just the caller times out.
                var work = Task.Run(async () =>
                {
                    try { return await _provider.QueryAsync(query, scope, token); }
                    finally { ReleaseQueryTurn(); }
                });
                try
                {
                    var indexed = await work.WaitAsync(_queryTimeout);
                    available = true;
                    foreach (var row in indexed.Take(60))
                        if (row.Path.Length <= 4096 && PathRules.Validate(row.Path) is null && row.Kind is "folder" or "file" or "app")
                            Add(row.Title, row.Path, row.Kind, "index", path: row.Path);
                }
                catch (Exception)
                {
                    cancellation.Cancel();
                    _ = work.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    note = "Windows 搜索索引不可用或查询超时；已保存入口与设置仍可使用。可在“搜索与索引”中检查索引范围。";
                }
            }
        }
        else note = "当前范围无需查询 Windows 索引。";
        if (fuzzyNames)
            note += " 模糊匹配仅应用于已保存文件/目录入口；Windows 索引仍按文件名词首和正文查询，不扫描未索引位置。";
        return new SearchResponse(results, available, note);
    }

    private static bool MatchesAppAlias(string query, string identity)
    {
        var alias = query.Trim();
        return AppAliasIdentities.TryGetValue(alias, out var identities) &&
            identities.Any(name => identity.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesSavedNameFuzzy(string query, string candidate)
    {
        var queryWords = Regex.Matches(query, @"[\p{L}\p{N}]+").Select(match => match.Value).ToArray();
        if (queryWords.Length == 0) return false;
        var candidateWords = Regex.Matches(candidate, @"[\p{L}\p{N}]+").Select(match => match.Value).ToArray();
        if (candidateWords.Length == 0) return false;
        if (queryWords.All(word => candidate.Contains(word, StringComparison.OrdinalIgnoreCase))) return true;
        return queryWords.Length == 1 && queryWords[0].Length >= 5 &&
            candidateWords.Any(word => IsSingleEditApart(queryWords[0], word));
    }

    private static bool IsSingleEditApart(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        if (Math.Abs(left.Length - right.Length) > 1) return false;
        left = left.ToUpperInvariant(); right = right.ToUpperInvariant();
        if (left.Length == right.Length)
        {
            var differences = new List<int>(2);
            for (var i = 0; i < left.Length && differences.Count <= 2; i++) if (left[i] != right[i]) differences.Add(i);
            if (differences.Count == 1) return true;
            return differences.Count == 2 && differences[1] == differences[0] + 1 &&
                left[differences[0]] == right[differences[1]] && left[differences[1]] == right[differences[0]];
        }
        var shorter = left.Length < right.Length ? left : right;
        var longer = left.Length < right.Length ? right : left;
        var shortIndex = 0; var longIndex = 0; var skipped = false;
        while (shortIndex < shorter.Length && longIndex < longer.Length)
        {
            if (shorter[shortIndex] == longer[longIndex]) { shortIndex++; longIndex++; continue; }
            if (skipped) return false;
            skipped = true; longIndex++;
        }
        return true;
    }

    private async Task<bool> WaitForQueryTurnAsync(string clientId)
    {
        PendingQuery pending;
        lock (_gate)
        {
            // One queued request per client: a newer input completes the obsolete waiter,
            // without adding another provider worker or waiting SemaphoreSlim task.
            if (_pendingQueries.Remove(clientId, out var obsolete)) obsolete.Ready.TrySetResult(false);
            if (!_queryActive) { _queryActive = true; return true; }
            if (_pendingQueries.Count >= 16) return false;
            pending = new PendingQuery();
            _pendingQueries.Add(clientId, pending);
        }
        try { return await pending.Ready.Task.WaitAsync(TimeSpan.FromMilliseconds(Math.Min(2000, _queryTimeout.TotalMilliseconds))); }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                // A completion racing this timeout already transferred ownership to us.
                // Accept it so that the single worker slot is never leaked.
                if (pending.Granted) return true;
                if (_pendingQueries.TryGetValue(clientId, out var current) && ReferenceEquals(current, pending))
                    _pendingQueries.Remove(clientId);
                pending.Ready.TrySetResult(false);
                return false;
            }
        }
    }

    private void ReleaseQueryTurn()
    {
        lock (_gate)
        {
            if (_pendingQueries.Count == 0) { _queryActive = false; return; }
            var next = _pendingQueries.First();
            _pendingQueries.Remove(next.Key);
            next.Value.Granted = true;
            next.Value.Ready.TrySetResult(true);
            // Ownership passes directly to the selected waiter; _queryActive stays true.
        }
    }

    public LaunchOutcome Open(string clientId, string requestId, string resultId)
    {
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(requestId) || clientId.Length > 200 || requestId.Length > 200 || string.IsNullOrEmpty(resultId))
            return LaunchOutcome.Fail("INVALID_REQUEST", "搜索打开参数无效。");
        Lazy<LaunchOutcome> work;
        lock (_gate)
        {
            var now = _clock();
            foreach (var key in _requests.Where(p => p.Value.Work.IsValueCreated && now - p.Value.At > TimeSpan.FromMinutes(5)).Select(p => p.Key).ToArray()) _requests.Remove(key);
            if (_requests.TryGetValue((clientId, requestId), out var previous)) work = previous.Work;
            else
            {
                if (!_capabilities.TryGetValue(resultId, out var capability) || capability.ClientId != clientId || capability.Expires <= now)
                    return LaunchOutcome.Fail("INVALID_REQUEST", "搜索结果已过期，请重新搜索。");
                // Do not evict live request records: keep idempotence for the whole capability lifetime.
                if (_requests.Count >= 256) return LaunchOutcome.Fail("INVALID_REQUEST", "打开请求过多，请稍后重试。");
                work = new Lazy<LaunchOutcome>(() => Resolve(capability), LazyThreadSafetyMode.ExecutionAndPublication);
                _requests[(clientId, requestId)] = (work, now);
            }
        }
        return work.Value;
    }

    private LaunchOutcome Resolve(Capability capability)
    {
        if (capability.ProjectId is not null && capability.ItemId is not null)
            return _launch.OpenItem(Guid.NewGuid().ToString("N"), capability.ProjectId, capability.ItemId);
        if (capability.Setting is { } setting && Settings.Any(s => s.Uri == setting))
            return LaunchPath(setting);
        if (capability.Path is not { } path || PathRules.Validate(path) is not null)
            return LaunchOutcome.Fail("INVALID_REQUEST", "搜索结果路径无效。");
        if (!_probes.Wait(0)) return LaunchOutcome.Fail("PATH_NOT_FOUND", "路径检查仍在进行，请稍后重试。");
        var probe = Task.Run(() => { try { return _probe.Exists(path); } finally { _probes.Release(); } });
        try
        {
            if (!probe.Wait(TimeSpan.FromSeconds(3)) || !probe.Result) return LaunchOutcome.Fail("PATH_NOT_FOUND", "搜索结果已移动或不可访问，请重新搜索。");
        }
        catch (AggregateException) { return LaunchOutcome.Fail("PATH_NOT_FOUND", "搜索结果不可访问。"); }
        return LaunchPath(path);
    }

    private LaunchOutcome LaunchPath(string path)
    {
        var error = _shell.TryLaunch(path);
        return error is null ? LaunchOutcome.Ok() : LaunchOutcome.Fail("ACCESS_DENIED", error);
    }
}
