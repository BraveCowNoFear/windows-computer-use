using WindowsComputerUse.Broker;

namespace WindowsComputerUse.Tests;

public sealed class InputServiceTests
{
    [Fact]
    public void ChordPlanner_AddsImpliedShiftForUppercasePrintableKey()
    {
        var plan = InputService.PlanChord("A");
        Assert.Collection(plan,
            shift => Assert.Equal((ushort)0x10, shift.VirtualKey),
            letter => Assert.Equal((ushort)0x41, letter.VirtualKey));
    }

    [Fact]
    public void ChordPlanner_CoversExtendedNavigationAndFunctionKeys()
    {
        var plan = InputService.PlanChord("rctrl+right+f24");
        Assert.Collection(plan,
            control => { Assert.Equal((ushort)0xA3, control.VirtualKey); Assert.True(control.Extended); },
            right => { Assert.Equal((ushort)0x27, right.VirtualKey); Assert.True(right.Extended); },
            function => { Assert.Equal((ushort)0x87, function.VirtualKey); Assert.False(function.Extended); });
    }

    [Fact]
    public void ChordPlanner_NormalizesModifierOrder()
    {
        var plan = InputService.PlanChord("a+ctrl");
        Assert.Collection(plan,
            control => Assert.Equal((ushort)0x11, control.VirtualKey),
            letter => Assert.Equal((ushort)0x41, letter.VirtualKey));
    }

    [Fact]
    public void ChordPlanner_OmitsAnAlreadyHeldModifierGroup()
    {
        var plan = InputService.PlanChord("A", "lshift");

        Assert.Collection(plan, letter => Assert.Equal((ushort)0x41, letter.VirtualKey));
    }

    [Fact]
    public void ChordPlanner_OmitsExplicitControlWhenRightControlIsHeld()
    {
        var plan = InputService.PlanChord("ctrl+s", "rctrl");

        Assert.Collection(plan, letter => Assert.Equal((ushort)0x53, letter.VirtualKey));
    }
}
