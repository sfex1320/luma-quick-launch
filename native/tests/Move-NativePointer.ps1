param([Parameter(Mandatory=$true)][int]$X,[Parameter(Mandatory=$true)][int]$Y)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class LumaPointerTest {
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
 public class Result { public int X,Y; public uint HitProcess; public string HitClass,ForegroundClass; }
 [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")] static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
 [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
 [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr w,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr w,StringBuilder s,int n);
 public static Result Move(int x,int y) { var old=SetThreadDpiAwarenessContext(new IntPtr(-4));try {if(!SetCursorPos(x,y))throw new Exception("SetCursorPos failed");Point p;GetCursorPos(out p);var hit=WindowFromPoint(p);uint pid;GetWindowThreadProcessId(hit,out pid);var hc=new StringBuilder(256);GetClassName(hit,hc,256);var fc=new StringBuilder(256);GetClassName(GetForegroundWindow(),fc,256);return new Result{X=p.X,Y=p.Y,HitProcess=pid,HitClass=hc.ToString(),ForegroundClass=fc.ToString()};}finally{SetThreadDpiAwarenessContext(old);} }
}
'@
ConvertTo-Json -InputObject ([LumaPointerTest]::Move($X,$Y)) -Compress
