using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public class HoverMovementGateTests
{
    [Fact]
    public void RelocationUnderStationaryPointerCannotStartDwell()
    {
        var gate = new HoverMovementGate();
        gate.Reset(true, 800, 0);
        Assert.False(gate.Observe(true, 800, 0, true, 2, 1));
        Assert.True(gate.Observe(true, 801, 0, true, 2, 1));
        gate.Reset(true, 801, 0);
        Assert.False(gate.Observe(true, 801, 0, true, 2, 1));
    }

    [Theory]
    [InlineData(true, 0, 0)]
    [InlineData(true, 0, 4)]
    [InlineData(true, 2, 2)]
    [InlineData(true, 2, 4)]
    [InlineData(false, 2, 1)]
    public void SyntheticOrUnknownMessageCannotBeginHover(bool sourceAvailable, int device, int origin)
    {
        var gate = new HoverMovementGate();
        gate.Reset(true, 800, 100);
        Assert.False(gate.Observe(true, 800, 0, sourceAvailable, device, origin));
        Assert.False(gate.Observe(true, 800, 0, true, 2, 1));
        Assert.True(gate.Observe(true, 801, 0, true, 2, 1));
    }

    [Fact]
    public void MissingCursorSampleRequiresANewPhysicalBaseline()
    {
        var gate = new HoverMovementGate();
        gate.Reset(false, 0, 0);
        Assert.False(gate.Observe(true, -1500, 120, true, 2, 1));
        Assert.True(gate.Observe(true, -1499, 120, true, 2, 1));
        Assert.False(gate.Observe(false, 0, 0, true, 2, 1));
    }
}
