using System.IO;
using System.Text.Json;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;
namespace Luma.Host.Tests;
public sealed class ShortcutBridgeTests : IDisposable
{
    private sealed class Platform : IHotkeyPlatform
    {
        public bool Register(int id,uint modifiers,uint key)=>true;
        public void Unregister(int id) { }
    }
    private sealed class Windows : IWindowHost
    {
        public string Focused="dock";
        public bool IsShortcutClientFocused(string id)=>id==Focused;
        public void SyncDock(bool expanded,System.Windows.Rect[] rects,bool interacting=false,long? visibilityId=null) { }
        public void OpenSettings(string section) { }
        public IntPtr SettingsOwnerHandle=>IntPtr.Zero;
        public void ShowDock() { }
    }
    private sealed class ReusePlatform : IWindowReusePlatform
    {
        public Action OnFind = () => { };
        public nint Window;
        public int Launches, Activations;
        public ReuseTarget Identify(string path) => new(path);
        public nint Find(ReuseTarget target) { OnFind(); return Window; }
        public bool Activate(nint window) { Activations++; return true; }
        public string? Launch(string path) { Launches++; return null; }
    }
    private readonly string _dir=Path.Combine(Path.GetTempPath(),"luma-shortcut-tests",Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData("shortcut.getStatus","{}")]
    [InlineData("shortcut.setRecording","{\"active\":true}")]
    public async Task SupportsStrictStatusAndRecordingRequests(string method,string parameters)
    {
        var store=new StateStore(_dir);store.Load();var router=new BridgeRouter(store,new LaunchService(store),new FakePicker(),new FakeWindows(),new ImmediateSync());
        var client=new FakeClient("settings");router.Attach(client);
        await router.HandleMessage(client,$$"""{"protocol":1,"type":"request","id":"r","method":"{{method}}","params":{{parameters}}}""");
        Assert.True(JsonDocument.Parse(client.Sent.Last()).RootElement.GetProperty("ok").GetBoolean());
    }
    [Fact] public void DeletedTargetDuringProbeMustNotLaunchStalePath()
    {
        var store=new StateStore(_dir);store.Load();store.Save(TestStates.OneProject("p",@"C:\old"),0);
        var shell=new FakeShell();var probe=new FakeProbe {Hook=_=>{var state=store.Current;state.Projects.Clear();store.Save(state,state.Revision);return true;}};
        var launcher=new LaunchService(store,shell,probe);
        Assert.False(launcher.OpenItem("r","p","main").Accepted);Assert.Empty(shell.Launched);
    }
    [Fact] public async Task BridgeResolvesSavedBindingAndRejectsWrongClientFocusAndRawPayload()
    {
        var store=new StateStore(_dir);store.Load();var state=StateValidator.EmptyState();
        state.Preferences.Shortcuts=[new ShortcutBinding{Id="saved",Code="KeyA",Action="edit"}];store.Save(state,0);
        var fired=0;using var shortcuts=new ShortcutService(()=>store.Current,new Platform(),(_,_)=>fired++,(_,_)=>Task.FromResult(true));
        var windows=new Windows();var router=new BridgeRouter(store,new LaunchService(store),new FakePicker(),windows,new ImmediateSync()){Shortcuts=shortcuts};
        var dock=new FakeClient("dock");var settings=new FakeClient("settings");router.Attach(dock);router.Attach(settings);
        async Task<JsonElement> Send(FakeClient client,string parameters)
        {
            await router.HandleMessage(client,$$"""{"protocol":1,"type":"request","id":"r","method":"shortcut.execute","params":{{parameters}}}""");
            return JsonDocument.Parse(client.Sent.Last()).RootElement.Clone();
        }
        Assert.False((await Send(settings,"{\"id\":\"saved\"}")).GetProperty("result").GetProperty("accepted").GetBoolean());
        windows.Focused="settings";Assert.False((await Send(dock,"{\"id\":\"saved\"}")).GetProperty("result").GetProperty("accepted").GetBoolean());
        windows.Focused="dock";Assert.False((await Send(dock,"{\"id\":\"saved\",\"path\":\"C:\\\\bad\"}")).GetProperty("ok").GetBoolean());
        Assert.False((await Send(new FakeClient("dock"),"{\"id\":\"saved\"}")).GetProperty("ok").GetBoolean());
        Assert.True((await Send(dock,"{\"id\":\"saved\"}")).GetProperty("result").GetProperty("accepted").GetBoolean());Assert.Equal(1,fired);
    }
    [Theory]
    [InlineData("binding", 0)]
    [InlineData("binding", 123)]
    [InlineData("item", 0)]
    [InlineData("item", 123)]
    [InlineData("exit", 0)]
    [InlineData("exit", 123)]
    public async Task SavedTargetOrBindingRevokedWhileStaFindBlockedCannotLaunchOrActivate(string revoke, int window)
    {
        var store=new StateStore(_dir);store.Load();var state=TestStates.OneProject("p",@"C:\saved");
        var binding=new ShortcutBinding{Id="saved",Code="KeyA",Ctrl=true,Scope="global",Action="item",ProjectId="p",ItemId="main"};
        state.Preferences.Shortcuts=[binding];store.Save(state,0);
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var os=new ReusePlatform{Window=window,OnFind=()=>{entered.Set();release.Wait(TimeSpan.FromSeconds(2));}};
        var probe=new FakeProbe();probe.Existing.Add(@"C:\saved");
        var launcher=new LaunchService(store,new RealShellExecutor(os),probe);var exiting=false;
        var work=Task.Run(()=>launcher.OpenItem("r","p","main",()=>!Volatile.Read(ref exiting)&&(store.Current.Preferences.Shortcuts??[]).Contains(binding)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
            if(revoke=="exit") Volatile.Write(ref exiting,true);
            else
            {
                var changed=store.Current;
                if(revoke=="binding") changed.Preferences.Shortcuts=[];
                else changed.Projects[0].Items[0].Path=@"C:\new";
                Assert.Equal(SaveOutcome.Saved,store.Save(changed,changed.Revision).Outcome);
            }
        }
        finally { release.Set(); }
        Assert.False((await work).Accepted);Assert.Equal(0,os.Launches);Assert.Equal(0,os.Activations);
    }
    public void Dispose() {if(Directory.Exists(_dir))Directory.Delete(_dir,true);}
}
