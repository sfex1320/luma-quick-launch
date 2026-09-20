namespace Luma.Host.Windows;

public enum DockVisibilityPhase { Hidden, AwaitingExpanded, Visible, Closing }

/// <summary>Native reveal is committed only by the first expanded layout acknowledgement.</summary>
public sealed class DockVisibilityState
{
    public const int CloseTimeoutMilliseconds = 1400;
    public DockVisibilityPhase Phase { get; private set; }
    public long VisibilityId { get; private set; }
    private long _closeDeadline;
    private void NextVisibilityId()
    {
        if (VisibilityId == 9007199254740991L) throw new InvalidOperationException("Visibility sequence exhausted.");
        VisibilityId++;
    }
    public void RequestShow() { NextVisibilityId(); Phase = DockVisibilityPhase.AwaitingExpanded; }
    public void RequestImmediateHide() { NextVisibilityId(); Hide(); }
    public void Hide() => Phase = DockVisibilityPhase.Hidden;
    public bool RequestHide(long now = 0)
    {
        if (Phase is DockVisibilityPhase.Hidden or DockVisibilityPhase.Closing) return false;
        NextVisibilityId();
        Phase = DockVisibilityPhase.Closing;
        _closeDeadline = now + CloseTimeoutMilliseconds;
        return true;
    }
    public bool CloseExpired(long now) => Phase == DockVisibilityPhase.Closing && now >= _closeDeadline;
    public bool AcceptsSync(long? visibilityId) => visibilityId is null || visibilityId == VisibilityId;
    public bool Acknowledge(bool expanded, long? visibilityId = null)
    {
        if (AcceptsSync(visibilityId) && Phase == DockVisibilityPhase.AwaitingExpanded && expanded)
        { Phase = DockVisibilityPhase.Visible; return true; }
        return false;
    }
    public bool ShouldHide(bool expanded, long? visibilityId = null) => AcceptsSync(visibilityId) && !expanded &&
        Phase is DockVisibilityPhase.Visible or DockVisibilityPhase.Closing;
}
