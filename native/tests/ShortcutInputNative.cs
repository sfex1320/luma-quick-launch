using System;using System.Text;using System.Collections.Generic;using System.Runtime.InteropServices;using System.Threading;
public static class LumaShortcutInput {
 [StructLayout(LayoutKind.Sequential)] public struct Rect{public int Left,Top,Right,Bottom;}
 [StructLayout(LayoutKind.Sequential)] public struct Point{public int X,Y;}
 [StructLayout(LayoutKind.Sequential)] struct MonitorInfo{public int Size;public Rect Monitor,Work;public uint Flags;}
 [StructLayout(LayoutKind.Sequential)] struct Keyboard{public ushort Vk,Scan;public uint Flags,Time;public UIntPtr Extra;}
 [StructLayout(LayoutKind.Sequential)] struct Mouse{public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
 [StructLayout(LayoutKind.Explicit)] struct Union{[FieldOffset(0)]public Keyboard K;[FieldOffset(0)]public Mouse M;}
 [StructLayout(LayoutKind.Sequential)] struct Input{public uint Type;public Union U;}
 delegate bool EnumProc(IntPtr w,IntPtr p);
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p,IntPtr x);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr w,out uint p);
 [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr w);
 [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr w,int n);
 [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr w,IntPtr after,int x,int y,int cx,int cy,uint flags);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr w);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr w,out Rect r);
 [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr w,ref Point p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr w,StringBuilder s,int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr w,StringBuilder s,int n);
 [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr w,uint flags);
 [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m,ref MonitorInfo info);
 [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
 [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr w,uint flag);
 [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint n,Input[] input,int size);
 [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
 [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr w,int id,uint modifiers,uint key);
 [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr w,int id);
 public sealed class Window {public long hwnd;public string title;public Rect bounds;public Point origin;public bool visible;}
 public static uint ForegroundPid(){uint pid;GetWindowThreadProcessId(GetForegroundWindow(),out pid);return pid;}
 public static bool Fullscreen(){SetThreadDpiAwarenessContext(new IntPtr(-4));var w=GetForegroundWindow();var cls=new StringBuilder(256);GetClassName(w,cls,256);if(cls.ToString()=="Progman"||cls.ToString()=="WorkerW"||cls.ToString()=="Shell_TrayWnd")return false;Rect r;GetWindowRect(w,out r);var info=new MonitorInfo{Size=Marshal.SizeOf(typeof(MonitorInfo))};if(!GetMonitorInfo(MonitorFromWindow(w,2),ref info))throw new Exception("Cannot inspect foreground monitor");return r.Left<=info.Monitor.Left&&r.Top<=info.Monitor.Top&&r.Right>=info.Monitor.Right&&r.Bottom>=info.Monitor.Bottom;}
 public static List<Window> Inspect(uint pid){SetThreadDpiAwarenessContext(new IntPtr(-4));var list=new List<Window>();EnumWindows((w,p)=>{uint id;GetWindowThreadProcessId(w,out id);if(id!=pid)return true;var s=new StringBuilder(512);GetWindowText(w,s,512);Rect r;GetWindowRect(w,out r);var origin=new Point();ClientToScreen(w,ref origin);list.Add(new Window{hwnd=w.ToInt64(),title=s.ToString(),bounds=r,origin=origin,visible=IsWindowVisible(w)});return true;},IntPtr.Zero);return list;}
 static void ValidateWindow(long handle,uint pid){uint actual;GetWindowThreadProcessId(new IntPtr(handle),out actual);if(actual!=pid)throw new Exception("Window owner mismatch");if(Fullscreen())throw new Exception("Fullscreen foreground: input refused");}
 static void Send(Input input){if(SendInput(1,new[]{input},Marshal.SizeOf(typeof(Input)))!=1)throw new Exception("SendInput failed");}
 static void Key(ushort vk,bool up){Send(new Input{Type=1,U=new Union{K=new Keyboard{Vk=vk,Flags=up?2u:0u}}});}
 public static void Click(long handle,uint pid,int x,int y){SetThreadDpiAwarenessContext(new IntPtr(-4));ValidateWindow(handle,pid);var hit=GetAncestor(WindowFromPoint(new Point{X=x,Y=y}),2);if(hit!=new IntPtr(handle))throw new Exception("Click point is covered by another window");SetCursorPos(x,y);Send(new Input{Type=0,U=new Union{M=new Mouse{Flags=2}}});Send(new Input{Type=0,U=new Union{M=new Mouse{Flags=4}}});}
 public static void Focus(long handle,uint pid){ValidateWindow(handle,pid);var w=new IntPtr(handle);ShowWindow(w,9);SetForegroundWindow(w);if(GetForegroundWindow()==w)return;SetWindowPos(w,IntPtr.Zero,0,0,0,0,0x0013);Rect r;GetWindowRect(w,out r);Click(handle,pid,r.Left+80,r.Top+85);Thread.Sleep(100);if(GetForegroundWindow()!=w)throw new Exception("Could not focus owned test window");}
 public static void Chord(long handle,uint pid,ushort key,uint modifiers,int repeats){ValidateWindow(handle,pid);if(GetForegroundWindow()!=new IntPtr(handle))throw new Exception("Input target is not foreground");foreach(var vk in new[]{0x10,0x11,0x12,0x5b,0x5c})if((GetAsyncKeyState(vk)&0x8000)!=0)throw new Exception("Physical modifier held: input refused");try{if((modifiers&2)!=0)Key(0x11,false);if((modifiers&4)!=0)Key(0x10,false);if((modifiers&1)!=0)Key(0x12,false);for(int i=0;i<repeats;i++){Key(key,false);if(repeats>1)Thread.Sleep(75);}}finally{Key(key,true);if((modifiers&1)!=0)Key(0x12,true);if((modifiers&4)!=0)Key(0x10,true);if((modifiers&2)!=0)Key(0x11,true);}}
 public static bool Probe(uint modifiers,uint key){bool registered=RegisterHotKey(IntPtr.Zero,77,modifiers|0x4000,key);if(registered)UnregisterHotKey(IntPtr.Zero,77);return registered;}
}

