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

    [Theory]
    [InlineData("left", "left", 0x0002u, 0x0004u, 0u)]
    [InlineData("secondary", "right", 0x0008u, 0x0010u, 0u)]
    [InlineData("back", "x1", 0x0080u, 0x0100u, 1u)]
    [InlineData("forward", "x2", 0x0080u, 0x0100u, 2u)]
    public void MouseButtonPlanner_NormalizesButtonsAndNativeData(string input, string name, uint down, uint up, uint data)
    {
        var plan = InputService.PlanMouseButton(input);

        Assert.Equal(name, plan.Name);
        Assert.Equal(down, plan.Down);
        Assert.Equal(up, plan.Up);
        Assert.Equal(data, plan.Data);
    }
}
