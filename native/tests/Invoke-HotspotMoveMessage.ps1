param([Parameter(Mandatory=$true)][int]$TargetProcessId,[Parameter(Mandatory=$true)][long]$WindowHandle)
$ErrorActionPreference='Stop'
Add-Type @'
using System;using System.Text;using System.Runtime.InteropServices;
public static class LumaSyntheticMove {
 [StructLayout(LayoutKind.Sequential)] struct Point {public int X,Y;}
 [StructLayout(LayoutKind.Sequential)] struct Rect {public int L,T,R,B;}
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr w,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr w,StringBuilder s,int n);
 [DllImport("user32.dll")] static extern bool GetPhysicalCursorPos(out Point p);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr w,out Rect r);
 [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
 [DllImport("user32.dll")] static extern bool PostMessage(IntPtr w,uint m,IntPtr p,IntPtr l);
 public static void Invoke(long handle,int pid) {
  var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
  try {
   var w=new IntPtr(handle);uint actual;GetWindowThreadProcessId(w,out actual);
   var name=new StringBuilder(100);GetClassName(w,name,100);
   if(actual!=pid||name.ToString()!="LumaHotzoneWnd")throw new Exception("Not the test host hotspot");
   Point p;Rect r;
   if(!GetPhysicalCursorPos(out p)||!GetWindowRect(w,out r))throw new Exception("Cursor/geometry unavailable");
   if(p.X>=r.L&&p.X<r.R&&p.Y>=r.T&&p.Y<r.B)throw new Exception("User cursor is in the test hotspot; stop without moving it");
   // Deliberately inconsistent: message says inside, real pointer remains outside.
   if(!PostMessage(w,0x02A3,IntPtr.Zero,IntPtr.Zero)||!PostMessage(w,0x0200,IntPtr.Zero,new IntPtr(0x00020002)))throw new Exception("Message delivery failed");
  } finally {SetThreadDpiAwarenessContext(previous);}
 }
}
'@
[LumaSyntheticMove]::Invoke($WindowHandle,$TargetProcessId)
