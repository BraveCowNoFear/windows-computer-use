using System.Runtime.InteropServices;
using WindowsComputerUse.Contracts;
using static WindowsComputerUse.Broker.NativeMethods;

namespace WindowsComputerUse.Broker;

public sealed class InputService
{
    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["backspace"] = 0x08,
        ["tab"] = 0x09,
        ["enter"] = 0x0D,
        ["return"] = 0x0D,
        ["shift"] = 0x10,
        ["ctrl"] = 0x11,
        ["control"] = 0x11,
        ["alt"] = 0x12,
        ["escape"] = 0x1B,
        ["esc"] = 0x1B,
        ["space"] = 0x20,
        ["pageup"] = 0x21,
        ["pagedown"] = 0x22,
        ["end"] = 0x23,
        ["home"] = 0x24,
        ["left"] = 0x25,
        ["up"] = 0x26,
        ["right"] = 0x27,
        ["down"] = 0x28,
        ["insert"] = 0x2D,
        ["delete"] = 0x2E,
        ["win"] = 0x5B,
        ["meta"] = 0x5B,
        ["apps"] = 0x5D,
        ["f1"] = 0x70,
        ["f2"] = 0x71,
        ["f3"] = 0x72,
        ["f4"] = 0x73,
        ["f5"] = 0x74,
        ["f6"] = 0x75,
        ["f7"] = 0x76,
        ["f8"] = 0x77,
        ["f9"] = 0x78,
        ["f10"] = 0x79,
        ["f11"] = 0x7A,
        ["f12"] = 0x7B
    };

    public void Click(int x, int y, string button = "left", int count = 1)
    {
        if (count is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(count));
        NativeMethods.SetCursorPos(x, y);
        var (down, up) = button.ToLowerInvariant() switch
        {
            "left" or "l" => (MouseeventfLeftdown, MouseeventfLeftup),
            "right" or "r" => (MouseeventfRightdown, MouseeventfRightup),
            "middle" or "m" => (MouseeventfMiddledown, MouseeventfMiddleup),
            _ => throw new ArgumentException("button must be left, right, or middle")
        };
        for (var i = 0; i < count; i++)
        {
            SendMouse(down);
            SendMouse(up);
            if (i + 1 < count) Thread.Sleep(70);
        }
    }

    public void Drag(int fromX, int fromY, int toX, int toY, int durationMs = 300)
    {
        NativeMethods.SetCursorPos(fromX, fromY);
        SendMouse(MouseeventfLeftdown);
        var steps = Math.Clamp(durationMs / 12, 4, 100);
        for (var i = 1; i <= steps; i++)
        {
            var progress = i / (double)steps;
            NativeMethods.SetCursorPos(
                (int)Math.Round(fromX + (toX - fromX) * progress),
                (int)Math.Round(fromY + (toY - fromY) * progress));
            Thread.Sleep(Math.Max(1, durationMs / steps));
        }
        SendMouse(MouseeventfLeftup);
    }

    public void Scroll(int x, int y, int vertical, int horizontal = 0)
    {
        NativeMethods.SetCursorPos(x, y);
        if (vertical != 0) SendMouse(MouseeventfWheel, unchecked((uint)(vertical * 120)));
        if (horizontal != 0) SendMouse(MouseeventfHwheel, unchecked((uint)(horizontal * 120)));
    }

    public void TypeText(string text)
    {
        foreach (var codeUnit in text)
        {
            SendKeyboard(0, codeUnit, KeyeventfUnicode);
            SendKeyboard(0, codeUnit, KeyeventfUnicode | KeyeventfKeyup);
        }
    }

    public void PressChord(string chord)
    {
        var keys = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ToVirtualKey).ToArray();
        if (keys.Length == 0) throw new ArgumentException("key chord is empty");
        foreach (var key in keys) SendKeyboard(key, '\0', 0);
        for (var index = keys.Length - 1; index >= 0; index--) SendKeyboard(keys[index], '\0', KeyeventfKeyup);
    }

    public (int X, int Y) WindowPoint(WindowDescriptor window, int x, int y, bool relative) =>
        relative ? (window.Bounds.X + x, window.Bounds.Y + y) : (x, y);

    private static ushort ToVirtualKey(string key)
    {
        if (NamedKeys.TryGetValue(key, out var result)) return result;
        if (key.Length == 1)
        {
            var scan = NativeMethods.VkKeyScanW(key[0]);
            if (scan != -1) return unchecked((ushort)(scan & 0xFF));
        }
        throw new ArgumentException($"Unsupported key name: {key}");
    }

    private static void SendMouse(uint flags, uint data = 0)
    {
        var input = new Input
        {
            Type = InputMouse,
            Union = new InputUnion { Mouse = new MouseInput { Flags = flags, MouseData = data } }
        };
        EnsureSent([input]);
    }

    private static void SendKeyboard(ushort virtualKey, char scanCode, uint flags)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInput { VirtualKey = virtualKey, ScanCode = scanCode, Flags = flags }
            }
        };
        EnsureSent([input]);
    }

    private static void EnsureSent(Input[] inputs)
    {
        if (NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException($"SendInput failed with Win32 error {Marshal.GetLastWin32Error()}.");
    }
}
