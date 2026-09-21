// Compiled only into each smoke run's temporary directory; no user software is used.
using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
public sealed class ShortcutInputProbe : Form
{
    [DllImport("user32.dll", SetLastError=true)] static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    readonly string root=AppDomain.CurrentDomain.BaseDirectory;
    readonly JavaScriptSerializer json=new JavaScriptSerializer();
    readonly TextBox input=new TextBox { Multiline=true, Dock=DockStyle.Fill };
    readonly Timer timer=new Timer { Interval=100 };
    string last="";
    bool held;
    void Log(object data) { File.AppendAllText(Path.Combine(root,"events.jsonl"),json.Serialize(data)+Environment.NewLine); }
    public ShortcutInputProbe()
    {
        Text="Luma shortcut fixture "+new DirectoryInfo(root).Name;
        Width=520;Height=260;StartPosition=FormStartPosition.Manual;Left=80;Top=350;
        Controls.Add(input);
        input.KeyDown+=(s,e)=>Log(new { type="key", code=(int)e.KeyCode, ctrl=e.Control, shift=e.Shift, alt=e.Alt });
        Shown+=(s,e)=>{ input.Focus(); Log(new {type="ready",pid=System.Diagnostics.Process.GetCurrentProcess().Id}); };
        timer.Tick+=(s,e)=>ReadCommand();timer.Start();
    }
    void ReadCommand()
    {
        var command=Path.Combine(root,"command.json");if(!File.Exists(command))return;
        Dictionary<string,object> value;
        try { value=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(command)); } catch(IOException) {return;} catch(ArgumentException) {return;}
        var serial=(string)value["serial"];Guid parsed;if(serial==last||!Guid.TryParse(serial,out parsed))return;last=serial;
        var op=(string)value["op"];bool ok=false;
        if(op=="hold") { if(held)UnregisterHotKey(Handle,42);held=RegisterHotKey(Handle,42,Convert.ToUInt32(value["modifiers"])|0x4000,Convert.ToUInt32(value["key"]));ok=held; }
        else if(op=="release") {if(held)UnregisterHotKey(Handle,42);held=false;ok=true;}
        else if(op=="close") {ok=true;BeginInvoke(new Action(Close));}
        File.WriteAllText(Path.Combine(root,"reply-"+serial+".json"),json.Serialize(new{ok=ok,op=op}));
    }
    protected override void WndProc(ref Message m) { if(m.Msg==0x0312){Log(new{type="hotkey",id=m.WParam.ToInt32()});}base.WndProc(ref m); }
    protected override void OnFormClosed(FormClosedEventArgs e) {timer.Stop();if(held)UnregisterHotKey(Handle,42);base.OnFormClosed(e);}
    [STAThread] public static void Main()
    {
        var root=AppDomain.CurrentDomain.BaseDirectory;
        File.AppendAllText(Path.Combine(root,"launches.jsonl"),new JavaScriptSerializer().Serialize(new{pid=System.Diagnostics.Process.GetCurrentProcess().Id})+Environment.NewLine);
        Application.EnableVisualStyles();Application.Run(new ShortcutInputProbe());
    }
}
