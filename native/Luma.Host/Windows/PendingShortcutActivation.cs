using Luma.Host.Bridge;
namespace Luma.Host.Windows;
public sealed record ShortcutActivation(ShortcutBinding Binding,long Serial);
public sealed class PendingShortcutActivation
{
    private ShortcutActivation? _pending;
    private long _deadline;
    public void Request(ShortcutBinding binding,long serial,long now) { _pending=new(binding,serial);_deadline=now+5000; }
    public ShortcutActivation? Take(AppState state,bool ready,long now)
    {
        if(!ready) return null;
        var pending=_pending;_pending=null;
        return pending is not null && now<=_deadline && (state.Preferences.Shortcuts??[]).Contains(pending.Binding) &&
            ShortcutRules.HasTarget(state,pending.Binding) ? pending : null;
    }
    public void Clear() => _pending=null;
}
