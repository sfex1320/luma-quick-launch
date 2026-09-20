using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

// An actual Windows OLE source, not WebView Input.dispatchDragEvent. It owns only
// fixture paths, never injects mouse-button presses, and always restores the cursor.
internal static class Program
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("ole32.dll")] private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("ole32.dll")] private static extern int DoDragDrop(
        [MarshalAs(UnmanagedType.Interface)] ComDataObject data,
        [MarshalAs(UnmanagedType.Interface)] IDropSource source, uint effects, out uint effect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);

    [ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escapePressed, uint keyState);
        [PreserveSig] int GiveFeedback(uint effect);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Source : IDropSource
    {
        public volatile int Decision;
        public uint LastEffect;
        public int FeedbackCount;
        public int QueryCount;
        public int QueryContinueDrag(bool escapePressed, uint keyState)
        {
            if (++QueryCount == 1) Emit(new { stage = "source-query", keyState });
            return escapePressed || Decision == 2 ? 0x40101 : Decision == 1 ? 0x40100 : 0;
        }
        public int GiveFeedback(uint effect) { LastEffect = effect; if (++FeedbackCount == 1) Emit(new { stage = "source-feedback", effect }); return 0x40102; }
    }

    private sealed record Command(int X, int Y, int HoldMilliseconds = 1800);
    private static void Emit(object value) => Console.WriteLine(JsonSerializer.Serialize(value));

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 4 || !File.Exists(args[0]) && !Directory.Exists(args[0]))
        { Console.Error.WriteLine("Usage: OleDragProbe <fixture-path> <edge-x> <edge-y> <command-json-path>"); return 2; }
        var x = int.Parse(args[1]); var y = int.Parse(args[2]); var commandPath = args[3];
        var dpi = SetThreadDpiAwarenessContext(-4);
        GetCursorPos(out var original);
        Emit(new { stage = "started", originalX = original.X, originalY = original.Y });
        var initialized = OleInitialize(0) >= 0;
        using var stop = new CancellationTokenSource();
        Thread? route = null;
        try
        {
            if (!initialized) throw new InvalidOperationException("OleInitialize failed");
            using var sourceWindow = new Form { Text = "Luma OLE Test Source", FormBorderStyle = FormBorderStyle.FixedToolWindow,
                ShowInTaskbar = false, TopMost = true, StartPosition = FormStartPosition.Manual,
                Bounds = new System.Drawing.Rectangle(x - 60, y + 220, 120, 80) };
            sourceWindow.Show();
            Application.DoEvents();
            SetThreadDpiAwarenessContext(-4);
            SetWindowPos(sourceWindow.Handle, -1, x - 60, y + 220, 120, 80, 0x0040);
            Application.DoEvents();
            var data = new DataObject(DataFormats.FileDrop, new[] { Path.GetFullPath(args[0]) });
            var source = new Source();
            SetCursorPos(x, y + 260);
            var initialHit = WindowFromPoint(new Point { X = x, Y = y + 260 });
            Emit(new { stage = "source-window", handle = sourceWindow.Handle.ToInt64(), hit = initialHit.ToInt64() });
            if (initialHit != sourceWindow.Handle) throw new InvalidOperationException("Source window was not the initial pointer target");
            route = new Thread(() =>
            {
                SetThreadDpiAwarenessContext(-4);
                var elapsed = Stopwatch.StartNew();
                Thread.Sleep(350);
                SetCursorPos(x, y);
                Emit(new { stage = "edge-entered", x, y });
                Command? command = null;
                while (!stop.IsCancellationRequested && elapsed.ElapsedMilliseconds < 15000)
                {
                    try
                    {
                        if (File.Exists(commandPath)) command = JsonSerializer.Deserialize<Command>(File.ReadAllText(commandPath));
                    }
                    catch (IOException) { }
                    catch (JsonException) { }
                    if (command != null) break;
                    // OLE receives actual pointer movement while it pumps messages.
                    SetCursorPos(x + (elapsed.ElapsedMilliseconds / 100 % 2 == 0 ? 0 : 1), y);
                    Thread.Sleep(100);
                }
                if (command != null && !stop.IsCancellationRequested)
                {
                    for (var step = 1; step <= 12 && !stop.IsCancellationRequested; step++)
                    {
                        SetCursorPos(x + (command.X - x) * step / 12, y + (command.Y - y) * step / 12);
                        Thread.Sleep(45);
                    }
                    Emit(new { stage = "webview-entered", command.X, command.Y });
                    Thread.Sleep(Math.Clamp(command.HoldMilliseconds, 0, 5000));
                    source.Decision = 1;
                    SetCursorPos(command.X + 1, command.Y);
                }
                else source.Decision = 2;
                // Bounded cancellation even if a target does not accept the transfer.
                while (!stop.IsCancellationRequested && elapsed.ElapsedMilliseconds < 22000)
                {
                    GetCursorPos(out var current); SetCursorPos(current.X + 1, current.Y);
                    Thread.Sleep(80); SetCursorPos(current.X, current.Y); Thread.Sleep(80);
                }
                if (!stop.IsCancellationRequested)
                {
                    source.Decision = 2;
                    SetCursorPos(x, y + 260);
                }
            }) { IsBackground = true };
            route.Start();
            Emit(new { stage = "calling-ole" });
            var hr = DoDragDrop((ComDataObject)data, source, 1, out var effect);
            stop.Cancel(); route.Join(2000);
            Emit(new { stage = "completed", hresult = $"0x{hr:X8}", effect, source.LastEffect, source.FeedbackCount });
            return hr == 0x40100 && effect == 1 ? 0 : 1;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 2; }
        finally
        {
            stop.Cancel(); route?.Join(2000);
            SetCursorPos(original.X, original.Y);
            if (initialized) OleUninitialize();
            SetThreadDpiAwarenessContext(dpi);
        }
    }
}
