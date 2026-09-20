using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Luma.Host.Interop;
using Xunit;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Luma.Host.Tests;

public class OleHotspotTests
{
    [Fact]
    public void FileDragRevealsButNeverReadsOrAcceptsTheFiles()
    {
        var reveals = 0;
        var target = new OleFileDropTarget(() => reveals++);
        var data = new ProbeDataObject(true);
        uint effect = 7;
        Assert.Equal(0, target.DragEnter(data, 1, default, ref effect));
        Assert.Equal(1, reveals);
        Assert.Equal(0u, effect);
        effect = 7;
        Assert.Equal(0, target.DragOver(1, default, ref effect));
        Assert.Equal(0u, effect);
        effect = 7;
        Assert.Equal(0, target.Drop(data, 0, default, ref effect));
        Assert.Equal(0u, effect);
        Assert.Equal(1, reveals);
        Assert.Equal(0, data.Reads);
        Assert.Equal(0, target.DragLeave());
    }

    [Fact]
    public void TextDragDoesNotRevealAndLaterFileDragStillWorks()
    {
        var reveals = 0;
        var target = new OleFileDropTarget(() => reveals++);
        uint effect = 7;
        target.DragEnter(new ProbeDataObject(false), 1, default, ref effect);
        Assert.Equal(0, reveals);
        Assert.Equal(0u, effect);
        target.DragLeave();
        target.DragEnter(new ProbeDataObject(true), 1, default, ref effect);
        Assert.Equal(1, reveals);
    }

    [Fact]
    public void NativeHotspotsRegisterPerWindowAndExposeOleVtable()
    {
        OnSta(() =>
        {
            using var first = new NativeHotspotWindow();
            using var second = new NativeHotspotWindow();
            first.Create(-30000, -30000, 400, 4);
            second.Create(-29000, -30000, 400, 4);
            Assert.NotEqual(IntPtr.Zero, first.Handle);
            Assert.NotEqual(first.Handle, second.Handle);
            var target = new OleFileDropTarget(() => { });
            const int AlreadyRegistered = unchecked((int)0x80040101);
            Assert.Equal(AlreadyRegistered, OleDropRegistration.RegisterDragDrop(first.Handle, target));
            Assert.Equal(AlreadyRegistered, OleDropRegistration.RegisterDragDrop(second.Handle, target));
            var unknown = Marshal.GetIUnknownForObject(target);
            try
            {
                var id = typeof(IOleDropTarget).GUID;
                Assert.Equal(0, Marshal.QueryInterface(unknown, in id, out var pointer));
                Marshal.Release(pointer);
            }
            finally { Marshal.Release(unknown); }
            // Removing one monitor must release only its own registration / window.
            first.Dispose();
            Assert.Equal(IntPtr.Zero, first.Handle);
            Assert.Equal(AlreadyRegistered, OleDropRegistration.RegisterDragDrop(second.Handle, target));
        });
    }

    [Fact]
    public void PhysicalHitRegionKeepsGapsAndRoundedCornersTransparent()
    {
        OnSta(() =>
        {
            using var window = new NativeHotspotWindow();
            window.Create(-30000, -30000, 1000, 500);
            Assert.True(Win32.GetWindowRect(window.Handle, out var bounds));
            var region = Win32.CreateRoundRectRgn(100, 20, 500, 180, 40, 40);
            Assert.NotEqual(0, Win32.SetWindowRgn(window.Handle, region, false)); // HWND owns HRGN now
            Assert.True(DockHitTest.Contains(window.Handle, bounds.Left + 200, bounds.Top + 80));
            Assert.False(DockHitTest.Contains(window.Handle, bounds.Left + 10, bounds.Top + 80));
            Assert.False(DockHitTest.Contains(window.Handle, bounds.Left + 100, bounds.Top + 20));
            Assert.False(DockHitTest.Contains(window.Handle, bounds.Left + 800, bounds.Top + 80));
            Assert.False(DockHitTest.Contains(IntPtr.Zero, 200, 80));
        });
    }

    private static void OnSta(Action work)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { work(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA test did not complete");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ProbeDataObject(bool file) : ComDataObject
    {
        public int Reads { get; private set; }
        public int QueryGetData(ref FORMATETC format) => file && format.cfFormat == 15 &&
            format.tymed == TYMED.TYMED_HGLOBAL && format.dwAspect == DVASPECT.DVASPECT_CONTENT && format.lindex == -1
            ? 0 : unchecked((int)0x80040064);
        public void GetData(ref FORMATETC format, out STGMEDIUM medium) { Reads++; throw new NotSupportedException(); }
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) { Reads++; throw new NotSupportedException(); }
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; return 1; }
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) { }
        public int EnumDAdvise(out IEnumSTATDATA? result) { result = null; return unchecked((int)0x80040003); }
    }
}
