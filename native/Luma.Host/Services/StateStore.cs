using System.IO;
using System.Text.Json;
using Luma.Host.Bridge;

namespace Luma.Host.Services;

public enum StoreOutcome
{
    Loaded,
    LoadedFromBackup,
    EmptyNewInstall,
    Corrupted,
}

public sealed class LoadResult
{
    public StoreOutcome Outcome { get; init; }
    public AppState? State { get; init; }
    /// <summary>Outcome=Corrupted 时的中文说明，用于 IO_ERROR message。</summary>
    public string Message { get; init; } = "";

    public static LoadResult FromState(AppState state, StoreOutcome outcome) => new() { Outcome = outcome, State = state };
    public static LoadResult Failed(string message) => new() { Outcome = StoreOutcome.Corrupted, Message = message };
}

public enum SaveOutcome
{
    Saved,
    RevisionConflict,
    InvalidState,
    IoError,
}

public sealed class SaveResult
{
    public SaveOutcome Outcome { get; init; }
    public AppState? State { get; init; }
    public string Message { get; init; } = "";

    public static SaveResult Ok(AppState state) => new() { Outcome = SaveOutcome.Saved, State = state };
    public static SaveResult Conflict() => new() { Outcome = SaveOutcome.RevisionConflict, Message = "配置已在其他窗口更新，请重新载入后再保存。" };
    public static SaveResult Invalid(List<string> errors) => new() { Outcome = SaveOutcome.InvalidState, Message = "配置数据不合法：" + string.Join("；", errors.Take(5)) };
    public static SaveResult Io(string message) => new() { Outcome = SaveOutcome.IoError, Message = "配置写入失败：" + message };
}

/// <summary>
/// 状态存储：主文件 %LOCALAPPDATA%/Luma/state.json，写入临时文件后原子替换，保留一份有效备份 state.json.bak。
/// 保存锁覆盖「检查 expectedRevision → 校验 → 写盘 → 更新内存权威版本」全过程。
/// </summary>
public sealed class StateStore
{
    public const string AppFolderName = "Luma";
    public const string MainFileName = "state.json";
    public const string BackupFileName = "state.json.bak";
    public const string TempFileName = "state.json.tmp";

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _mainPath;
    private readonly string _backupPath;
    private readonly string _tempPath;

    private AppState _current = StateValidator.EmptyState();
    private bool _recoveredFromBackup;
    public string? LoadError { get; private set; }

    public event Action<AppState>? StateChanged;

    public StateStore(string? baseDirectory = null)
    {
        _directory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);
        _mainPath = Path.Combine(_directory, MainFileName);
        _backupPath = Path.Combine(_directory, BackupFileName);
        _tempPath = Path.Combine(_directory, TempFileName);
    }

    public string MainPath => _mainPath;
    public string BackupPath => _backupPath;

    /// <summary>内存中的权威状态（最后一次成功加载或保存的结果）。</summary>
    public AppState Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>
    /// 启动读取：主文件有效用主文件；主文件损坏尝试备份；主文件不存在且备份不存在是新安装返回空状态；
    /// 两份都存在但都损坏返回 Corrupted，绝不静默丢失用户数据。
    /// </summary>
    public LoadResult Load()
    {
        lock (_gate)
        {
            LoadError = null;
            _recoveredFromBackup = false;
            var mainExists = File.Exists(_mainPath);
            var backupExists = File.Exists(_backupPath);
            if (!mainExists && !backupExists)
            {
                _current = StateValidator.EmptyState();
                return LoadResult.FromState(_current, StoreOutcome.EmptyNewInstall);
            }

            if (mainExists)
            {
                var parsed = TryReadFile(_mainPath);
                if (parsed is not null)
                {
                    _current = parsed;
                    return LoadResult.FromState(parsed, StoreOutcome.Loaded);
                }
                Log.Warn($"主配置文件损坏，尝试备份：{_mainPath}");
            }

            if (backupExists)
            {
                var parsed = TryReadFile(_backupPath);
                if (parsed is not null)
                {
                    _current = parsed;
                    Log.Warn($"已从备份恢复配置：{_backupPath}");
                    _recoveredFromBackup = true;
                    return LoadResult.FromState(parsed, StoreOutcome.LoadedFromBackup);
                }
                Log.Error($"备份配置文件也损坏：{_backupPath}");
            }

            LoadError = "配置文件与备份无法读取或已损坏，已停止保存以保护原文件。";
            return LoadResult.Failed(LoadError);
        }
    }

    /// <summary>乐观并发保存：expectedRevision 必须等于当前权威 revision，且 state.revision 必须等于 expectedRevision。</summary>
    public SaveResult Save(AppState incoming, long expectedRevision)
    {
        if (incoming is null) return SaveResult.Invalid(new List<string> { "state 不能为空。" });
        lock (_gate)
        {
            if (LoadError is not null) return SaveResult.Io(LoadError);
            if (incoming.Revision != expectedRevision)
                return SaveResult.Invalid(new List<string> { "state.revision 必须等于 expectedRevision。" });
            if (expectedRevision != _current.Revision)
                return SaveResult.Conflict();

            var next = CloneWithRevision(incoming, expectedRevision + 1);
            var errors = StateValidator.Validate(next);
            if (errors.Count > 0) return SaveResult.Invalid(errors);

            try
            {
                Directory.CreateDirectory(_directory);
                var json = JsonSerializer.Serialize(next, ContractsJson.Options);
                // WriteThrough 尽量确保掉电时数据完整；随后 File.Replace 原子交换。
                using (var stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(_mainPath))
                {
                    // 原子替换并把旧主文件留作备份；ignoreMetadataErrors 避免不同卷语义差异。
                    File.Replace(_tempPath, _mainPath, _recoveredFromBackup ? null : _backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(_tempPath, _mainPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Log.Error($"保存配置失败：{ex.Message}");
                TryCleanupTemp();
                return SaveResult.Io(ex.Message);
            }

            _current = next;
            _recoveredFromBackup = false;
            StateChanged?.Invoke(next);
            return SaveResult.Ok(next);
        }
    }

    private void TryCleanupTemp()
    {
        try { if (File.Exists(_tempPath)) File.Delete(_tempPath); }
        catch { /* 清理失败不影响错误回报 */ }
    }

    /// <summary>读取并校验单个文件；任何 JSON/校验失败返回 null（视为损坏）。</summary>
    private static AppState? TryReadFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            var state = JsonSerializer.Deserialize<AppState>(json, ContractsJson.Options);
            if (state is null) return null;
            if (StateValidator.Validate(state).Count > 0) return null;
            return state;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static AppState CloneWithRevision(AppState state, long revision) => new()
    {
        SchemaVersion = state.SchemaVersion,
        Revision = revision,
        Projects = state.Projects,
        Preferences = state.Preferences,
    };
}
