using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;
namespace Luma.Host.Tests;
public sealed class ShortcutServiceTests
{
    private sealed class Platform : IHotkeyPlatform
    {
        public Dictionary<int,(uint Modifiers,uint Key)> Registered { get; } = new();
        public bool Fail;
        public bool Register(int id,uint modifiers,uint key) { if(Fail) return false; Registered.Add(id,(modifiers,key)); return true; }
        public void Unregister(int id) => Registered.Remove(id);
        public int UserId => Registered.Single(x=>x.Value.Key==0x41).Key;
    }
    private static ShortcutBinding Binding(string action="dock",string scope="global") => new() {Id="a",Code="KeyA",Ctrl=scope=="global",Scope=scope,Action=action};
    [Fact] public async Task ReconcileUnregistersOldIdsAndNeverDispatchesStaleMessages()
    {
        var p=new Platform(); var state=StateValidator.EmptyState(); state.Preferences.Shortcuts=[Binding()]; var fired=new List<string>();
        using var service=new ShortcutService(()=>state,p,(b,s)=>fired.Add(b.Id),(_,_)=>Task.FromResult(true));
        service.Reconcile(); var old=p.UserId;
        state.Preferences.Shortcuts=[Binding() with {Action="search"}]; service.Reconcile();
        Assert.False(p.Registered.ContainsKey(old));
        Assert.False(await service.HandleHotkeyAsync(old));
        Assert.True(await service.HandleHotkeyAsync(p.UserId)); Assert.Equal(new[]{"a"},fired);
        service.Dispose(); Assert.Empty(p.Registered); Assert.False(await service.HandleHotkeyAsync(old));
    }
    [Fact] public void RegistrationFailureVisibleAndNoRepeatFlagRequired()
    {
        var p=new Platform{Fail=true}; var state=StateValidator.EmptyState(); state.Preferences.Shortcuts=[Binding()];
        using var service=new ShortcutService(()=>state,p,(_,_)=>{},(_,_)=>Task.FromResult(true));
        service.Reconcile(); Assert.False(Assert.Single(service.GetStatus()).Registered); Assert.NotEmpty(Assert.Single(service.GetStatus()).Message);
        p.Fail=false;service.Reconcile();Assert.True(Assert.Single(service.GetStatus()).Registered);
        Assert.Equal(0x4002u,p.Registered[p.UserId].Modifiers);
    }
    [Fact] public async Task CurrentSavedTargetAndPanelFocusAreRequiredAndDirectoryActionsRefused()
    {
        var p=new Platform();var state=TestStates.OneProject("p",@"C:\saved");var item=state.Projects[0].Items[0];
        state.Preferences.Shortcuts=[Binding("item","panel") with {ProjectId="p",ItemId=item.Id}];
        var launches=new List<string>();using var service=new ShortcutService(()=>state,p,(_,_)=>{},(b,_)=>{launches.Add(b.ItemId!);return Task.FromResult(true);});
        service.Reconcile();Assert.False(await service.ExecutePanelAsync("a",false));Assert.False(await service.ExecutePanelAsync("unknown",true));
        Assert.True(await service.ExecutePanelAsync("a",true));Assert.Single(launches);
        state.Projects.Clear();Assert.False(await service.ExecutePanelAsync("a",true));Assert.Contains("失效",Assert.Single(service.GetStatus()).Message);
        state.Preferences.Shortcuts=[Binding("copyAddress","panel")];service.Reconcile();Assert.False(await service.ExecutePanelAsync("a",true));
    }
    [Fact] public async Task HeldKeyAndConcurrentLaunchesAreDeduplicated()
    {
        var p=new Platform();var state=StateValidator.EmptyState();state.Preferences.Shortcuts=[Binding()];var count=0;
        using var service=new ShortcutService(()=>state,p,(_,_)=>count++,(_,_)=>Task.FromResult(true));service.Reconcile();
        Assert.True(await service.HandleHotkeyAsync(p.UserId));Assert.False(await service.HandleHotkeyAsync(p.UserId));Assert.Equal(1,count);
        Assert.False(await service.ExecutePanelAsync("a",true));
    }
    [Fact] public async Task RecordingOwnershipExpiryAndDetachRestoreUsingFreshIds()
    {
        var now=DateTimeOffset.UtcNow;var p=new Platform();var state=StateValidator.EmptyState();state.Preferences.Shortcuts=[Binding()];
        using var service=new ShortcutService(()=>state,p,(_,_)=>{},(_,_)=>Task.FromResult(true),()=>now);service.Reconcile();var old=p.UserId;
        service.SetRecording("settings",true);Assert.Empty(p.Registered);Assert.False(await service.HandleHotkeyAsync(old));
        service.SetRecording("other",false);Assert.Empty(p.Registered);
        now+=TimeSpan.FromSeconds(31);service.ExpireRecordings();Assert.NotEqual(old,p.UserId);
        service.SetRecording("settings",true);service.Detach("settings");Assert.NotEmpty(p.Registered);
    }
    [Fact] public async Task AtMostFourLaunchesEnterAndRepeatedInFlightBindingIsRefused()
    {
        var p=new Platform();var state=TestStates.OneProject("p",@"C:\saved");
        state.Preferences.Shortcuts=Enumerable.Range(0,5).Select(i=>Binding("item","panel") with{Id="s"+i,Code="Key"+(char)('A'+i),ProjectId="p",ItemId="main"}).ToList();
        var holds=Enumerable.Range(0,4).Select(_=>new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();var count=0;
        using var service=new ShortcutService(()=>state,p,(_,_)=>{},(_,_)=>holds[count++].Task);
        var running=Enumerable.Range(0,4).Select(i=>service.ExecutePanelAsync("s"+i,true)).ToArray();
        Assert.False(await service.ExecutePanelAsync("s0",true));Assert.False(await service.ExecutePanelAsync("s4",true));Assert.Equal(4,count);
        for(var i=0;i<4;i++){holds[i].SetResult(true);Assert.True(await running[i]);}
    }
    [Fact] public void UnchangedConfigurationKeepsRegistrationsAndRemovalReleasesGlobalTarget()
    {
        var p=new Platform();var state=TestStates.OneProject("p",@"C:\saved");state.Preferences.Shortcuts=[Binding("item") with{ProjectId="p",ItemId="main"}];
        using var service=new ShortcutService(()=>state,p,(_,_)=>{},(_,_)=>Task.FromResult(true));service.Reconcile();var id=p.UserId;
        service.Reconcile();Assert.Equal(id,p.UserId);state.Projects.Clear();service.Reconcile();
        Assert.False(p.Registered.ContainsKey(id));Assert.False(Assert.Single(service.GetStatus()).Registered);
    }
}

