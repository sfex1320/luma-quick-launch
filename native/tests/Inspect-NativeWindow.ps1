param([Parameter(Mandatory=$true)][int]$TargetProcessId)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class LumaNativeInspection {
 public delegate bool EnumProc(IntPtr w,IntPtr p);
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p,IntPtr x);
 [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr w,int index);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr w,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr w,StringBuilder s,int n);
 [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr w,IntPtr r);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr w,out Rect r);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr w);
 [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
 [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int a,int b,int c,int d);
 [DllImport("gdi32.dll")] static extern int GetRgnBox(IntPtr r,out Rect box);
 [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr r);
 [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr w,int attr,out int value,int size);
 public class Snapshot { public string Title; public long Hwnd; public bool Visible,Topmost; public int RegionType; public Rect Bounds,Region; public int Backdrop; public uint OutsideHitProcess; public int ImmersiveDarkMode,CaptionColor,TextColor; public int ThemeAttributeResult,CaptionAttributeResult,TextAttributeResult; }
 public static List<Snapshot> Read(uint pid) {
 var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
 var list=new List<Snapshot>();
 try { EnumWindows((w,p)=>{
 uint id; GetWindowThreadProcessId(w,out id); if(id!=pid)return true;
 var title=new StringBuilder(256);GetWindowText(w,title,256);if(!title.ToString().StartsWith("Luma"))return true;
 var r=CreateRectRgn(0,0,0,0);
 try {Rect region,bounds; int backdrop; uint outside; var kind=GetWindowRgn(w,r);GetRgnBox(r,out region);GetWindowRect(w,out bounds);DwmGetWindowAttribute(w,38,out backdrop,4);
 var hit=WindowFromPoint(new Point{X=bounds.Left+20,Y=bounds.Top+200});GetWindowThreadProcessId(hit,out outside);
 int dark,caption,text; var themeResult=DwmGetWindowAttribute(w,20,out dark,4);var captionResult=DwmGetWindowAttribute(w,35,out caption,4);var textResult=DwmGetWindowAttribute(w,36,out text,4);
 list.Add(new Snapshot{Title=title.ToString(),Hwnd=w.ToInt64(),Visible=IsWindowVisible(w),Topmost=(GetWindowLongPtr(w,-20).ToInt64()&8)!=0,RegionType=kind,Bounds=bounds,Region=region,Backdrop=backdrop,OutsideHitProcess=outside,ImmersiveDarkMode=dark,CaptionColor=caption,TextColor=text,ThemeAttributeResult=themeResult,CaptionAttributeResult=captionResult,TextAttributeResult=textResult});
 } finally {DeleteObject(r);} return true;},IntPtr.Zero);return list;
 } finally {SetThreadDpiAwarenessContext(previous);}
 }
}
'@
ConvertTo-Json -InputObject @([LumaNativeInspection]::Read($TargetProcessId)) -Depth 5 -Compress
