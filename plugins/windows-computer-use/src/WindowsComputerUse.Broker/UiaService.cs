using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Broker;

public sealed class UiaService : IDisposable
{
    private readonly UIA3Automation _automation = new();
    private readonly WindowService _windows;
    private readonly InputService _input;
    private readonly Dictionary<string, ElementLocator> _elements = new(StringComparer.Ordinal);

    public UiaService(WindowService windows, InputService input)
    {
        _windows = windows;
        _input = input;
    }

    public WindowInspection Inspect(WindowDescriptor window, int limit = 400)
    {
        limit = Math.Clamp(limit, 1, 2000);
        var root = _automation.FromHandle(new nint(window.Id));
        var all = new List<AutomationElement> { root };
        try { all.AddRange(root.FindAllDescendants().Take(limit - 1)); } catch { }

        var controls = new List<ControlDescriptor>(all.Count);
        var duplicateKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        string? focused = null;
        foreach (var element in all.Take(limit))
        {
            var baseKey = SelectorKey(element);
            duplicateKeys.TryGetValue(baseKey, out var duplicateIndex);
            duplicateKeys[baseKey] = duplicateIndex + 1;
            var descriptor = Describe(element, controls.Count, window.Id, duplicateIndex);
            controls.Add(descriptor);
            if (descriptor.HasKeyboardFocus) focused = descriptor.Id;
        }

        var tree = string.Join(Environment.NewLine, controls.Select(control =>
            $"[{control.Index}] {control.ControlType} name=\"{Escape(control.Name)}\" automationId=\"{Escape(control.AutomationId)}\" " +
            $"id={control.Id} bounds=({control.Bounds.X},{control.Bounds.Y},{control.Bounds.Width},{control.Bounds.Height}) " +
            $"enabled={control.IsEnabled.ToString().ToLowerInvariant()} patterns=[{string.Join(',', control.Patterns)}]"));
        return new WindowInspection(
            window,
            $"obs-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow,
            tree,
            controls,
            focused);
    }

    public IReadOnlyList<ControlDescriptor> Find(WindowDescriptor window, JsonElement query, int limit = 50)
    {
        var inspection = Inspect(window, Math.Clamp(query.Int("scan_limit", 800), 1, 2000));
        var name = query.String("name");
        var nameContains = query.String("name_contains");
        var automationId = query.String("automation_id");
        var controlType = query.String("control_type");
        var className = query.String("class_name");
        var enabledOnly = query.Bool("enabled_only");
        return inspection.Controls.Where(control =>
            (name is null || string.Equals(control.Name, name, StringComparison.OrdinalIgnoreCase)) &&
            (nameContains is null || control.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) &&
            (automationId is null || string.Equals(control.AutomationId, automationId, StringComparison.OrdinalIgnoreCase)) &&
            (controlType is null || string.Equals(control.ControlType, controlType, StringComparison.OrdinalIgnoreCase)) &&
            (className is null || control.ClassName.Contains(className, StringComparison.OrdinalIgnoreCase)) &&
            (!enabledOnly || control.IsEnabled)).Take(Math.Clamp(limit, 1, 200)).ToArray();
    }

    public ActionResult Invoke(WindowDescriptor window, string? controlId, JsonElement query)
    {
        var element = Resolve(window, controlId, query);
        var before = Summary(element);
        var backend = "uia3-invoke";
        if (element.Patterns.Invoke.TryGetPattern(out var invoke)) invoke.Invoke();
        else if (element.Patterns.SelectionItem.TryGetPattern(out var selection)) selection.Select();
        else if (element.Patterns.Toggle.TryGetPattern(out var toggle)) toggle.Toggle();
        else if (element.Patterns.ExpandCollapse.TryGetPattern(out var expand)) expand.Expand();
        else
        {
            var rectangle = element.BoundingRectangle;
            _input.Click(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2);
            backend = "sendinput-center-click";
        }
        Thread.Sleep(120);
        return Verify("invoke", backend, window, controlId, query, before);
    }

    public ActionResult EnterText(WindowDescriptor window, string? controlId, JsonElement query, string text, bool append)
    {
        var element = Resolve(window, controlId, query);
        var before = Summary(element);
        var backend = "uia3-value";
        if (element.Patterns.Value.TryGetPattern(out var value) && !append)
        {
            value.SetValue(text);
        }
        else
        {
            element.Focus();
            Thread.Sleep(50);
            if (!append) _input.PressChord("ctrl+a");
            _input.TypeText(text);
            backend = "sendinput-unicode";
        }
        Thread.Sleep(100);
        return Verify("enter_text", backend, window, controlId, query, before);
    }

    public object WaitFor(WindowDescriptor window, JsonElement query, string state, int timeoutMs, int pollMs)
    {
        timeoutMs = Math.Clamp(timeoutMs, 50, 120_000);
        pollMs = Math.Clamp(pollMs, 25, 2_000);
        var started = Environment.TickCount64;
        IReadOnlyList<ControlDescriptor> matches = [];
        while (Environment.TickCount64 - started <= timeoutMs)
        {
            matches = Find(window, query, query.Int("limit", 20));
            var satisfied = state.ToLowerInvariant() switch
            {
                "exists" or "visible" => matches.Any(control => !control.IsOffscreen),
                "absent" or "hidden" => matches.Count == 0 || matches.All(control => control.IsOffscreen),
                "enabled" => matches.Any(control => control.IsEnabled),
                "focused" => matches.Any(control => control.HasKeyboardFocus),
                _ => throw new ArgumentException("state must be exists, absent, visible, hidden, enabled, or focused")
            };
            if (satisfied)
                return new { matched = true, state, elapsed_ms = Environment.TickCount64 - started, controls = matches };
            Thread.Sleep(pollMs);
        }
        return new { matched = false, state, elapsed_ms = Environment.TickCount64 - started, controls = matches };
    }

    public void ClearSession() => _elements.Clear();

    public void Dispose()
    {
        _elements.Clear();
        _automation.Dispose();
    }

    private ActionResult Verify(string action, string backend, WindowDescriptor window, string? controlId, JsonElement query, string before)
    {
        try
        {
            var afterElement = Resolve(window, controlId, query);
            var after = Summary(afterElement);
            var descriptor = Describe(afterElement, 0, window.Id, 0);
            return new ActionResult(true, action, backend,
                new ActionVerification(true, "uia3-reobserve", before, after, descriptor.Id), descriptor);
        }
        catch (Exception error)
        {
            return new ActionResult(true, action, backend,
                new ActionVerification(true, "window-reobserve-element-changed", before, error.Message, controlId));
        }
    }

    private AutomationElement Resolve(WindowDescriptor window, string? controlId, JsonElement query)
    {
        if (!string.IsNullOrWhiteSpace(controlId) && _elements.TryGetValue(controlId, out var cached))
        {
            try
            {
                _ = cached.Element.Name;
                return cached.Element;
            }
            catch
            {
                var refreshed = FindByLocator(window, cached);
                if (refreshed is not null)
                {
                    _elements[controlId] = cached with { Element = refreshed };
                    return refreshed;
                }
            }
        }

        var matches = Find(window, query, 2);
        if (matches.Count != 1)
            throw new InvalidOperationException(matches.Count == 0
                ? "No matching control was found. Reinspect the window before retrying."
                : "Control selector is ambiguous. Use the stable id returned by find_controls.");
        return _elements[matches[0].Id].Element;
    }

    private AutomationElement? FindByLocator(WindowDescriptor window, ElementLocator locator)
    {
        var root = _automation.FromHandle(new nint(window.Id));
        AutomationElement[] candidates;
        try { candidates = root.FindAllDescendants(); } catch { return null; }
        return candidates.FirstOrDefault(element =>
            string.Equals(Safe(() => element.AutomationId, ""), locator.AutomationId, StringComparison.Ordinal) &&
            string.Equals(Safe(() => element.Name, ""), locator.Name, StringComparison.Ordinal) &&
            string.Equals(Safe(() => element.ControlType.ToString(), ""), locator.ControlType, StringComparison.Ordinal));
    }

    private ControlDescriptor Describe(AutomationElement element, int index, long windowId, int duplicateIndex)
    {
        var name = Safe(() => element.Name ?? "", "");
        var automationId = Safe(() => element.AutomationId ?? "", "");
        var controlType = Safe(() => element.ControlType.ToString() ?? "Unknown", "Unknown");
        var className = Safe(() => element.ClassName ?? "", "");
        var rectangle = Safe(() => element.BoundingRectangle, System.Drawing.Rectangle.Empty);
        var bounds = new RectDto(
            rectangle.X, rectangle.Y,
            Math.Max(0, rectangle.Width), Math.Max(0, rectangle.Height));
        var selector = $"window={windowId};automationId={automationId};type={controlType};name={name};class={className};ordinal={duplicateIndex}";
        var id = $"wc-{windowId:x}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selector)))[..16].ToLowerInvariant()}";
        var patterns = GetPatterns(element);
        var descriptor = new ControlDescriptor(
            id, index, name, automationId, controlType, className, bounds,
            Safe(() => element.IsEnabled, false),
            Safe(() => element.IsOffscreen, true),
            Safe(() => element.Properties.HasKeyboardFocus.ValueOrDefault, false),
            patterns,
            selector);
        _elements[id] = new ElementLocator(windowId, name, automationId, controlType, className, duplicateIndex, element);
        return descriptor;
    }

    private static IReadOnlyList<string> GetPatterns(AutomationElement element)
    {
        var patterns = new List<string>();
        TryAdd(patterns, "invoke", () => element.Patterns.Invoke.IsSupported);
        TryAdd(patterns, "value", () => element.Patterns.Value.IsSupported);
        TryAdd(patterns, "text", () => element.Patterns.Text.IsSupported);
        TryAdd(patterns, "selection-item", () => element.Patterns.SelectionItem.IsSupported);
        TryAdd(patterns, "toggle", () => element.Patterns.Toggle.IsSupported);
        TryAdd(patterns, "expand-collapse", () => element.Patterns.ExpandCollapse.IsSupported);
        TryAdd(patterns, "scroll", () => element.Patterns.Scroll.IsSupported);
        return patterns;
    }

    private static void TryAdd(List<string> target, string name, Func<bool> supported)
    {
        try { if (supported()) target.Add(name); } catch { }
    }

    private static string SelectorKey(AutomationElement element) => string.Join('|',
        Safe(() => element.AutomationId ?? "", ""),
        Safe(() => element.ControlType.ToString() ?? "", ""),
        Safe(() => element.Name ?? "", ""),
        Safe(() => element.ClassName ?? "", ""));

    private static string Summary(AutomationElement element)
    {
        var bounds = Safe(() => element.BoundingRectangle.ToString(), "unavailable");
        return $"name={Safe(() => element.Name ?? "", "")};type={Safe(() => element.ControlType.ToString() ?? "", "")};bounds={bounds};enabled={Safe(() => element.IsEnabled, false)}";
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }

    private static string Escape(string? value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

    private sealed record ElementLocator(
        long WindowId,
        string Name,
        string AutomationId,
        string ControlType,
        string ClassName,
        int Ordinal,
        AutomationElement Element);
}
