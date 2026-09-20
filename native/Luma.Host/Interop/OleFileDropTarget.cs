using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Luma.Host.Interop;

[ComVisible(true), Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleDropTarget
{
    [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keyState, OlePoint point, ref uint effect);
    [PreserveSig] int DragOver(uint keyState, OlePoint point, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keyState, OlePoint point, ref uint effect);
}

[StructLayout(LayoutKind.Sequential)]
public struct OlePoint { public int X, Y; }

/// <summary>Reveal-only OLE target. Never reads paths, accepts a drop, or launches anything.</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class OleFileDropTarget(Action reveal) : IOleDropTarget
{
    public int DragEnter(IDataObject data, uint keyState, OlePoint point, ref uint effect)
    {
        effect = 0; // DROPEFFECT_NONE: the source must never move/delete its files here.
        try
        {
            var format = new FORMATETC
            { cfFormat = 15 /* CF_HDROP */, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            if (data.QueryGetData(ref format) == 0) reveal();
        }
        catch (Exception ex) { Log.Warn($"热区 OLE 拖入检查失败: {ex.Message}"); }
        return 0;
    }
    public int DragOver(uint keyState, OlePoint point, ref uint effect) { effect = 0; return 0; }
    public int DragLeave() { Log.Info("热区 OLE DragLeave"); return 0; }
    public int Drop(IDataObject data, uint keyState, OlePoint point, ref uint effect)
    { effect = 0; Log.Info("热区 OLE Drop 拒绝操作，仅允许拖入唤出"); return 0; }
}

internal sealed class OleDropRegistration : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly IOleDropTarget _target; // Keep CCW alive for the registered HWND.
    private bool _disposed;
    public OleDropRegistration(IntPtr hwnd, IOleDropTarget target)
    {
        Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
        try { Marshal.ThrowExceptionForHR(RegisterDragDrop(hwnd, target)); }
        catch { OleUninitialize(); throw; }
        _hwnd = hwnd; _target = target;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RevokeDragDrop(_hwnd);
        GC.KeepAlive(_target);
        OleUninitialize();
    }
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] internal static extern int RegisterDragDrop(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] IOleDropTarget target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(IntPtr hwnd);
}
