param([Parameter(Mandatory=$true)][int]$TargetProcessId,[Parameter(Mandatory=$true)][long]$WindowHandle)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;using System.Text;using System.Runtime.InteropServices;
public static class LumaHotspotClick {
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr w,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr w,StringBuilder s,int n);
 [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr w,uint m,IntPtr p,IntPtr l);
 public static void Click(long handle,int pid){var w=new IntPtr(handle);uint actual;GetWindowThreadProcessId(w,out actual);var name=new StringBuilder(100);GetClassName(w,name,100);if(actual!=pid||name.ToString()!="LumaHotzoneWnd")throw new Exception("Not the test host hotspot");SendMessage(w,0x0202,IntPtr.Zero,new IntPtr(0x00020002));}
}
'@
[LumaHotspotClick]::Click($WindowHandle,$TargetProcessId)
