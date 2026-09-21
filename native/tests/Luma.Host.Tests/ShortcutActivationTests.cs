using Luma.Host.Bridge;
using Luma.Host.Windows;
using Xunit;
namespace Luma.Host.Tests;
public sealed class ShortcutActivationTests
{
    [Fact] public void WaitsForLatestLayoutThenConsumesOnlyLatestActivationOnce()
    {
        var state=StateValidator.EmptyState();var first=new ShortcutBinding{Id="one",Code="KeyA",Action="edit"};var second=first with {Id="two",Code="KeyB"};
        state.Preferences.Shortcuts=[first,second];var queue=new PendingShortcutActivation();
        queue.Request(first,1,100);queue.Request(second,2,100);
        Assert.Null(queue.Take(state,false,100));var activation=queue.Take(state,true,100);
        Assert.NotNull(activation);Assert.Equal("two",activation.Binding.Id);Assert.Equal(2,activation.Serial);Assert.Null(queue.Take(state,true,100));
    }
    [Fact] public void DeletedReassignedAndExpiredActivationsNeverReplay()
    {
        var state=StateValidator.EmptyState();var binding=new ShortcutBinding{Id="one",Code="KeyA",Action="edit"};state.Preferences.Shortcuts=[binding];var queue=new PendingShortcutActivation();
        queue.Request(binding,1,100);state.Preferences.Shortcuts=[binding with{Action="dock"}];Assert.Null(queue.Take(state,true,100));
        state.Preferences.Shortcuts=[binding];queue.Request(binding,2,100);Assert.Null(queue.Take(state,true,6100));
        queue.Request(binding,3,100);queue.Clear();Assert.Null(queue.Take(state,true,100));
    }
}
