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
        ["printscreen"] = 0x2C,
        ["snapshot"] = 0x2C,
        ["win"] = 0x5B,
        ["lwin"] = 0x5B,
        ["rwin"] = 0x5C,
        ["meta"] = 0x5B,
        ["apps"] = 0x5D,
        ["contextmenu"] = 0x5D,
        ["pause"] = 0x13,
        ["capslock"] = 0x14,
        ["numlock"] = 0x90,
        ["scrolllock"] = 0x91,
        ["lshift"] = 0xA0,
        ["rshift"] = 0xA1,
        ["lctrl"] = 0xA2,
        ["rctrl"] = 0xA3,
        ["lalt"] = 0xA4,
        ["ralt"] = 0xA5,
        ["browserback"] = 0xA6,
        ["browserforward"] = 0xA7,
        ["volumemute"] = 0xAD,
        ["volumedown"] = 0xAE,
        ["volumeup"] = 0xAF,
        ["medianext"] = 0xB0,
        ["mediaprevious"] = 0xB1,
        ["mediastop"] = 0xB2,
        ["mediaplaypause"] = 0xB3,
        ["numpad0"] = 0x60,
        ["numpad1"] = 0x61,
        ["numpad2"] = 0x62,
        ["numpad3"] = 0x63,
        ["numpad4"] = 0x64,
        ["numpad5"] = 0x65,
        ["numpad6"] = 0x66,
        ["numpad7"] = 0x67,
        ["numpad8"] = 0x68,
        ["numpad9"] = 0x69,
        ["multiply"] = 0x6A,
        ["add"] = 0x6B,
        ["subtract"] = 0x6D,
        ["decimal"] = 0x6E,
        ["divide"] = 0x6F,
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
        ["f12"] = 0x7B,
        ["f13"] = 0x7C,
        ["f14"] = 0x7D,
        ["f15"] = 0x7E,
        ["f16"] = 0x7F,
        ["f17"] = 0x80,
        ["f18"] = 0x81,
        ["f19"] = 0x82,
        ["f20"] = 0x83,
        ["f21"] = 0x84,
        ["f22"] = 0x85,
        ["f23"] = 0x86,
        ["f24"] = 0x87
    };

    private static readonly Dictionary<string, char> PrintableAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["plus"] = '+',
        ["minus"] = '-',
        ["comma"] = ',',
        ["period"] = '.',
        ["slash"] = '/',
        ["backslash"] = '\\',
        ["semicolon"] = ';',
        ["quote"] = '\'',
        ["backtick"] = '`',
        ["leftbracket"] = '[',
        ["rightbracket"] = ']'
    };

    private static readonly HashSet<ushort> ExtendedKeys =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x2C,
        0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5, 0xA6, 0xA7,
        0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3
    ];

    private readonly Dictionary<ushort, HeldKey> _heldKeys = [];

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

    public (int X, int Y) PointerPosition()
    {
        if (!NativeMethods.GetCursorPos(out var point))
            throw new InvalidOperationException($"GetCursorPos failed with Win32 error {Marshal.GetLastWin32Error()}.");
        return (point.X, point.Y);
    }

    public void MovePointer(int x, int y, int durationMs = 0)
    {
        durationMs = Math.Clamp(durationMs, 0, 10_000);
        if (durationMs == 0)
        {
            EnsureCursorPosition(x, y);
            return;
        }
        var start = PointerPosition();
        var steps = Math.Clamp(durationMs / 12, 4, 240);
        for (var i = 1; i <= steps; i++)
        {
            var progress = i / (double)steps;
            EnsureCursorPosition(
                (int)Math.Round(start.X + (x - start.X) * progress),
                (int)Math.Round(start.Y + (y - start.Y) * progress));
            Thread.Sleep(Math.Max(1, durationMs / steps));
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
        var held = _heldKeys.Values.Select(item => item.Stroke).ToArray();
        var sequence = PlanChordStrokes(chord)
            .Where(stroke => !IsAlreadyHeld(stroke, held))
            .ToArray();
        if (sequence.Length == 0) return;
        var inputs = sequence.Select(stroke => KeyboardInputFor(stroke, keyUp: false))
            .Concat(sequence.AsEnumerable().Reverse().Select(stroke => KeyboardInputFor(stroke, keyUp: true)))
            .ToArray();
        try { EnsureSent(inputs); }
        catch
        {
            foreach (var stroke in sequence.AsEnumerable().Reverse())
            {
                try { EnsureSent([KeyboardInputFor(stroke, keyUp: true)]); } catch { }
            }
            throw;
        }
    }

    internal static IReadOnlyList<PlannedKey> PlanChord(string chord, params string[] heldKeys)
    {
        var held = heldKeys.Select(ToKeyStroke).ToArray();
        return PlanChordStrokes(chord)
            .Where(stroke => !IsAlreadyHeld(stroke, held))
            .Select(stroke => new PlannedKey(stroke.VirtualKey, stroke.Extended))
            .ToArray();
    }

    private static List<KeyStroke> PlanChordStrokes(string chord)
    {
        var strokes = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ToKeyStroke).ToArray();
        if (strokes.Length == 0) throw new ArgumentException("key chord is empty");

        var explicitModifiers = strokes.Where(stroke => ModifierGroup(stroke.VirtualKey) != 0)
            .Select(stroke => ModifierGroup(stroke.VirtualKey)).ToHashSet();
        var sequence = new List<KeyStroke>();
        foreach (var modifier in strokes.SelectMany(ImpliedModifiers).DistinctBy(stroke => ModifierGroup(stroke.VirtualKey)))
        {
            if (!explicitModifiers.Contains(ModifierGroup(modifier.VirtualKey))) sequence.Add(modifier);
        }
        sequence.AddRange(strokes.Where(stroke => ModifierGroup(stroke.VirtualKey) != 0));
        sequence.AddRange(strokes.Where(stroke => ModifierGroup(stroke.VirtualKey) == 0));
        return sequence.DistinctBy(stroke => stroke.VirtualKey).ToList();
    }

    public void KeyDown(string key)
    {
        var stroke = ToKeyStroke(key);
        if (stroke.ModifierMask != 0)
            throw new ArgumentException("key_down requires an explicit key name; hold shift/ctrl/alt separately for modified printable keys.");
        if (_heldKeys.ContainsKey(stroke.VirtualKey)) return;
        EnsureSent([KeyboardInputFor(stroke, keyUp: false)]);
        _heldKeys[stroke.VirtualKey] = new HeldKey(key, stroke);
    }

    public void KeyUp(string key)
    {
        var stroke = ToKeyStroke(key);
        if (stroke.ModifierMask != 0)
            throw new ArgumentException("key_up requires the same explicit key name used by key_down.");
        if (_heldKeys.TryGetValue(stroke.VirtualKey, out var held)) stroke = held.Stroke;
        EnsureSent([KeyboardInputFor(stroke, keyUp: true)]);
        _heldKeys.Remove(stroke.VirtualKey);
    }

    public int ReleaseAllKeys()
    {
        var held = _heldKeys.Values.Reverse().ToArray();
        if (held.Length == 0) return 0;
        Exception? failure = null;
        var released = 0;
        foreach (var item in held)
        {
            try
            {
                EnsureSent([KeyboardInputFor(item.Stroke, keyUp: true)]);
                _heldKeys.Remove(item.Stroke.VirtualKey);
                released++;
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }
        if (failure is not null) throw new InvalidOperationException($"Failed to release {_heldKeys.Count} held key(s).", failure);
        return released;
    }

    public IReadOnlyList<string> HeldKeys => _heldKeys.Values.Select(item => item.Name).ToArray();

    public (int X, int Y) WindowPoint(WindowDescriptor window, int x, int y, bool relative) =>
        relative ? (window.Bounds.X + x, window.Bounds.Y + y) : (x, y);

    private static KeyStroke ToKeyStroke(string key)
    {
        if (NamedKeys.TryGetValue(key, out var named)) return new KeyStroke(named, 0, ExtendedKeys.Contains(named));
        if (PrintableAliases.TryGetValue(key, out var alias)) key = alias.ToString();
        if (key.Length == 1)
        {
            var scan = NativeMethods.VkKeyScanW(key[0]);
            if (scan != -1)
            {
                var virtualKey = unchecked((ushort)(scan & 0xFF));
                return new KeyStroke(virtualKey, (scan >> 8) & 0x07, ExtendedKeys.Contains(virtualKey));
            }
        }
        throw new ArgumentException($"Unsupported key name: {key}");
    }

    private static IEnumerable<KeyStroke> ImpliedModifiers(KeyStroke stroke)
    {
        if ((stroke.ModifierMask & 1) != 0) yield return new KeyStroke(0x10, 0, false);
        if ((stroke.ModifierMask & 2) != 0) yield return new KeyStroke(0x11, 0, false);
        if ((stroke.ModifierMask & 4) != 0) yield return new KeyStroke(0x12, 0, false);
    }

    private static int ModifierGroup(ushort virtualKey) => virtualKey switch
    {
        0x10 or 0xA0 or 0xA1 => 1,
        0x11 or 0xA2 or 0xA3 => 2,
        0x12 or 0xA4 or 0xA5 => 4,
        _ => 0
    };

    private static bool IsAlreadyHeld(KeyStroke stroke, IReadOnlyCollection<KeyStroke> held)
    {
        var modifierGroup = ModifierGroup(stroke.VirtualKey);
        return held.Any(item => item.VirtualKey == stroke.VirtualKey ||
            (modifierGroup != 0 && ModifierGroup(item.VirtualKey) == modifierGroup));
    }

    private static Input KeyboardInputFor(KeyStroke stroke, bool keyUp)
    {
        var flags = (stroke.Extended ? KeyeventfExtendedkey : 0) | (keyUp ? KeyeventfKeyup : 0);
        return new Input
        {
            Type = InputKeyboard,
            Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = stroke.VirtualKey, Flags = flags } }
        };
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

    private static void EnsureCursorPosition(int x, int y)
    {
        if (!NativeMethods.SetCursorPos(x, y))
            throw new InvalidOperationException($"SetCursorPos failed with Win32 error {Marshal.GetLastWin32Error()}.");
    }

    private sealed record KeyStroke(ushort VirtualKey, int ModifierMask, bool Extended);
    private sealed record HeldKey(string Name, KeyStroke Stroke);
    internal sealed record PlannedKey(ushort VirtualKey, bool Extended);
}
