using Luma.Host.Bridge;
using Luma.Host.Interop;
namespace Luma.Host.Services;

public interface IHotkeyPlatform { bool Register(int id,uint modifiers,uint key); void Unregister(int id); }
public sealed class WindowsHotkeyPlatform(IntPtr handle) : IHotkeyPlatform
{
    public bool Register(int id,uint modifiers,uint key) => Win32.RegisterHotKey(handle,id,modifiers,key);
    public void Unregister(int id) => Win32.UnregisterHotKey(handle,id);
}
public sealed record ShortcutStatus(string Id,bool Registered,string Message);

/// <summary>UI-thread owned registrations. No hook or polling; admissions bound asynchronous launcher work.</summary>
public sealed class ShortcutService : IDisposable
{
    private const string BuiltinId = "__luma_builtin_search";
    private static readonly ShortcutBinding Builtin = new() { Id=BuiltinId,Code="Space",Ctrl=true,Alt=true,Scope="global",Action="search" };
    private readonly Func<AppState> _state;
    private readonly IHotkeyPlatform _platform;
    private readonly Action<ShortcutBinding,long> _dispatch;
    private readonly Func<ShortcutBinding,string,Task<bool>> _launch;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<int,ShortcutBinding> _registered = new();
    private readonly Dictionary<string,DateTimeOffset> _recorders = new(StringComparer.Ordinal);
    private readonly Dictionary<string,DateTimeOffset> _lastFired = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private int _nextId=2;
    private long _serial;
    private bool _disposed;
    public event Action? RecordingChanged;
    public DateTimeOffset? NextRecordingExpiry => _recorders.Count==0 ? null : _recorders.Values.Min();
    public ShortcutService(Func<AppState> state,IHotkeyPlatform platform,Action<ShortcutBinding,long> dispatch,Func<ShortcutBinding,string,Task<bool>> launch,Func<DateTimeOffset>? now=null)
        { _state=state;_platform=platform;_dispatch=dispatch;_launch=launch;_now=now??(()=>DateTimeOffset.UtcNow); }
    private ShortcutBinding[] Saved => (_state().Preferences.Shortcuts ?? []).ToArray();
    public void Reconcile()
    {
        if (_disposed) return;
        var state=_state();
        var bindings=(state.Preferences.Shortcuts??[]).Where(b=>b.Scope=="global" && ShortcutRules.HasTarget(state,b)).Append(Builtin).ToArray();
        if (_recorders.Count>0) bindings=[];
        foreach(var pair in _registered.ToArray())
            if(!bindings.Contains(pair.Value)) { _platform.Unregister(pair.Key);_registered.Remove(pair.Key); }
        foreach(var binding in bindings)
        {
            if(_registered.Values.Contains(binding)) continue;
            // Never reuse an id: queued WM_HOTKEY cannot activate a newly assigned binding.
            if(_nextId>0xbfff) { Log.Warn("快捷键注册编号已用尽，请重新启动 Luma。");break; }
            var id=_nextId++;
            if(_platform.Register(id,ShortcutRules.Modifiers(binding)|0x4000,ShortcutRules.VirtualKey(binding.Code))) _registered.Add(id,binding);
            else Log.Warn($"快捷键注册失败 id={binding.Id}，可能已被其他应用占用。");
        }
        var savedIds=Saved.Select(b=>b.Id).Append(BuiltinId).ToHashSet(StringComparer.Ordinal);
        foreach(var id in _lastFired.Keys.Where(id=>!savedIds.Contains(id)).ToArray()) _lastFired.Remove(id);
    }
    public IReadOnlyList<ShortcutStatus> GetStatus()
    {
        var state=_state();
        return (state.Preferences.Shortcuts??[]).Select(b=>
        {
            if(!ShortcutRules.HasTarget(state,b)) return new ShortcutStatus(b.Id,false,"目标已失效，请重新选择。");
            if(_recorders.Count>0) return new ShortcutStatus(b.Id,false,"录制按键期间暂停。");
            if(b.Scope=="panel") return new ShortcutStatus(b.Id,!_disposed,"面板有焦点时生效。");
            var registered=!_disposed && _registered.Values.Contains(b);
            return new ShortcutStatus(b.Id,registered,registered?"全局快捷键已注册。":"注册失败：可能被占用或编号耗尽，请更换按键或重启。");
        }).ToArray();
    }
    public Task<bool> HandleHotkeyAsync(int id)
    {
        if(_disposed || _recorders.Count>0 || !_registered.TryGetValue(id,out var registered)) return Task.FromResult(false);
        var current=registered==Builtin ? Builtin : Saved.FirstOrDefault(b=>b.Id==registered.Id);
        return current!=registered ? Task.FromResult(false) : ExecuteAsync(current!);
    }
    public Task<bool> ExecutePanelAsync(string id,bool focused)
    {
        if(_disposed || !focused || _recorders.Count>0) return Task.FromResult(false);
        var binding=Saved.FirstOrDefault(b=>b.Id==id);
        if(binding?.Scope!="panel" || ShortcutRules.DirectoryActions.Contains(binding.Action)) return Task.FromResult(false);
        return ExecuteAsync(binding);
    }
    private async Task<bool> ExecuteAsync(ShortcutBinding binding)
    {
        if(!ShortcutRules.HasTarget(_state(),binding) || _inFlight.Count>=4 || _inFlight.Contains(binding.Id)) return false;
        var now=_now();
        if(_lastFired.TryGetValue(binding.Id,out var last) && now-last<TimeSpan.FromMilliseconds(250)) return false;
        _lastFired[binding.Id]=now;
        var serial=++_serial;
        if(binding.Action!="item") { _dispatch(binding,serial);return true; }
        _inFlight.Add(binding.Id);
        try { return await _launch(binding,"shortcut:"+serial); }
        catch(Exception ex) { Log.Warn($"快捷键启动失败：{ex.Message}");return false; }
        finally { _inFlight.Remove(binding.Id); }
    }
    public void SetRecording(string owner,bool active)
    {
        if(_disposed) return;
        if(active) _recorders[owner]=_now()+TimeSpan.FromSeconds(30); else _recorders.Remove(owner);
        Reconcile();RecordingChanged?.Invoke();
    }
    public void ExpireRecordings()
    {
        var now=_now();
        foreach(var pair in _recorders.Where(p=>p.Value<=now).ToArray()) _recorders.Remove(pair.Key);
        Reconcile();RecordingChanged?.Invoke();
    }
    public void Detach(string owner) => SetRecording(owner,false);
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        foreach(var id in _registered.Keys) _platform.Unregister(id);
        _registered.Clear();_recorders.Clear();RecordingChanged?.Invoke();
    }
}
