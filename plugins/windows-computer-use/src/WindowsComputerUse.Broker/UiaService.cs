using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
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
        var controls = new List<ControlDescriptor>(Math.Min(limit, 400));
        var rootSegment = new LocatorSegment(string.Empty, "WindowRoot", string.Empty, string.Empty, 0);
        var pending = new Queue<PendingElement>();
        pending.Enqueue(new PendingElement(root, null, 0, [rootSegment]));
        string? focused = null;
        string? documentText = null;
        string? selectedText = null;
        while (pending.Count > 0 && controls.Count < limit)
        {
            var current = pending.Dequeue();
            var children = FindChildren(current.Element);
            var parentId = current.ParentIndex is null ? null : controls[current.ParentIndex.Value].Id;
            var descriptor = Describe(
                current.Element,
                controls.Count,
                window.Id,
                parentId,
                current.Depth,
                children.Length,
                current.Path);
            controls.Add(descriptor);
            if (descriptor.HasKeyboardFocus)
            {
                focused = descriptor.Id;
                var text = ReadText(current.Element);
                documentText = text.DocumentText ?? descriptor.Value;
                selectedText = text.SelectedText;
            }
            else if (documentText is null && descriptor.ControlType == "Document")
            {
                documentText = ReadText(current.Element).DocumentText;
            }

            var duplicateKeys = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var child in children)
            {
                var baseKey = SelectorKey(child);
                duplicateKeys.TryGetValue(baseKey, out var ordinal);
                duplicateKeys[baseKey] = ordinal + 1;
                var path = current.Path.Concat([Segment(child, ordinal)]).ToArray();
                pending.Enqueue(new PendingElement(child, descriptor.Index, current.Depth + 1, path));
            }
        }

        var tree = string.Join(Environment.NewLine, controls.Select(control =>
            $"{new string(' ', control.Depth * 2)}[{control.Index}] {control.ControlType} name=\"{Escape(control.Name)}\" automationId=\"{Escape(control.AutomationId)}\" " +
            $"id={control.Id} bounds=({control.Bounds.X},{control.Bounds.Y},{control.Bounds.Width},{control.Bounds.Height}) " +
            $"enabled={control.IsEnabled.ToString().ToLowerInvariant()} children={control.ChildCount}{StateText(control)} patterns=[{string.Join(',', control.Patterns)}]"));
        var selectedControlIds = controls.Where(control => control.IsSelected == true).Select(control => control.Id).ToArray();
        return new WindowInspection(
            window,
            $"obs-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow,
            tree,
            controls,
            focused,
            documentText,
            selectedText,
            selectedControlIds);
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

    public ActionResult PerformSecondaryAction(WindowDescriptor window, string? controlId, JsonElement query, string action)
    {
        var element = Resolve(window, controlId, query);
        var before = Summary(element);
        var normalized = action.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        var backend = $"uia3-{normalized}";
        switch (normalized)
        {
            case "focus" or "raise":
                element.Focus();
                break;
            case "select":
                Require(element.Patterns.SelectionItem.TryGetPattern(out var select), action);
                select!.Select();
                break;
            case "add_to_selection":
                Require(element.Patterns.SelectionItem.TryGetPattern(out var add), action);
                add!.AddToSelection();
                break;
            case "remove_from_selection":
                Require(element.Patterns.SelectionItem.TryGetPattern(out var remove), action);
                remove!.RemoveFromSelection();
                break;
            case "toggle":
                Require(element.Patterns.Toggle.TryGetPattern(out var toggle), action);
                toggle!.Toggle();
                break;
            case "expand":
                Require(element.Patterns.ExpandCollapse.TryGetPattern(out var expand), action);
                expand!.Expand();
                break;
            case "collapse":
                Require(element.Patterns.ExpandCollapse.TryGetPattern(out var collapse), action);
                collapse!.Collapse();
                break;
            case "scroll_up":
                Scroll(element, ScrollAmount.NoAmount, ScrollAmount.LargeDecrement, action);
                break;
            case "scroll_down":
                Scroll(element, ScrollAmount.NoAmount, ScrollAmount.LargeIncrement, action);
                break;
            case "scroll_left":
                Scroll(element, ScrollAmount.LargeDecrement, ScrollAmount.NoAmount, action);
                break;
            case "scroll_right":
                Scroll(element, ScrollAmount.LargeIncrement, ScrollAmount.NoAmount, action);
                break;
            default:
                throw new ArgumentException("action must be focus, raise, select, add_to_selection, remove_from_selection, toggle, expand, collapse, scroll_up, scroll_down, scroll_left, or scroll_right");
        }
        Thread.Sleep(200);
        return Verify("perform_secondary_action", backend, window, controlId, query, before);
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
            var path = _elements.Values.FirstOrDefault(locator => ReferenceEquals(locator.Element, afterElement))?.Path
                ?? [Segment(afterElement, 0)];
            var parentId = path.Count > 1 ? IdFor(window.Id, path.Take(path.Count - 1)) : null;
            var descriptor = Describe(afterElement, 0, window.Id, parentId, path.Count - 1, FindChildren(afterElement).Length, path);
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
        if (window.Id != locator.WindowId || locator.Path.Count == 0) return null;
        var current = _automation.FromHandle(new nint(window.Id));
        foreach (var segment in locator.Path.Skip(1))
        {
            var matches = FindChildren(current).Where(element => SegmentMatches(element, segment)).ToArray();
            if (segment.Ordinal < 0 || segment.Ordinal >= matches.Length) return null;
            current = matches[segment.Ordinal];
        }
        return current;
    }

    private ControlDescriptor Describe(
        AutomationElement element,
        int index,
        long windowId,
        string? parentId,
        int depth,
        int childCount,
        IReadOnlyList<LocatorSegment> path)
    {
        var name = Safe(() => element.Name ?? "", "");
        var automationId = Safe(() => element.AutomationId ?? "", "");
        var controlType = Safe(() => element.ControlType.ToString() ?? "Unknown", "Unknown");
        var className = Safe(() => element.ClassName ?? "", "");
        var rectangle = Safe(() => element.BoundingRectangle, System.Drawing.Rectangle.Empty);
        var bounds = new RectDto(
            rectangle.X, rectangle.Y,
            Math.Max(0, rectangle.Width), Math.Max(0, rectangle.Height));
        var selector = SelectorFor(windowId, path);
        var id = IdFor(windowId, path);
        var patterns = GetPatterns(element);
        var state = ReadPatternState(element);
        var descriptor = new ControlDescriptor(
            id, index, parentId, depth, childCount, name, automationId, controlType, className, bounds,
            Safe(() => element.IsEnabled, false),
            Safe(() => element.IsOffscreen, true),
            Safe(() => element.Properties.HasKeyboardFocus.ValueOrDefault, false),
            state.Value,
            state.IsReadOnly,
            state.IsSelected,
            state.ToggleState,
            state.ExpandCollapseState,
            state.HorizontalScrollPercent,
            state.VerticalScrollPercent,
            patterns,
            selector);
        _elements[id] = new ElementLocator(windowId, path, element);
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

    private static PatternState ReadPatternState(AutomationElement element)
    {
        string? value = null;
        bool? isReadOnly = null;
        bool? isSelected = null;
        string? toggleState = null;
        string? expandCollapseState = null;
        double? horizontalScrollPercent = null;
        double? verticalScrollPercent = null;
        try
        {
            if (element.Patterns.Value.TryGetPattern(out var valuePattern))
            {
                value = Truncate(valuePattern.Value.ValueOrDefault, 4096);
                isReadOnly = valuePattern.IsReadOnly.ValueOrDefault;
            }
        }
        catch { }
        try
        {
            if (element.Patterns.SelectionItem.TryGetPattern(out var selection))
                isSelected = selection.IsSelected.ValueOrDefault;
        }
        catch { }
        try
        {
            if (element.Patterns.Toggle.TryGetPattern(out var toggle))
                toggleState = toggle.ToggleState.ValueOrDefault.ToString();
        }
        catch { }
        try
        {
            if (element.Patterns.ExpandCollapse.TryGetPattern(out var expand))
                expandCollapseState = expand.ExpandCollapseState.ValueOrDefault.ToString();
        }
        catch { }
        try
        {
            if (element.Patterns.Scroll.TryGetPattern(out var scroll))
            {
                horizontalScrollPercent = scroll.HorizontalScrollPercent.ValueOrDefault;
                verticalScrollPercent = scroll.VerticalScrollPercent.ValueOrDefault;
            }
        }
        catch { }
        return new PatternState(value, isReadOnly, isSelected, toggleState, expandCollapseState, horizontalScrollPercent, verticalScrollPercent);
    }

    private static TextSnapshot ReadText(AutomationElement element)
    {
        try
        {
            if (!element.Patterns.Text.TryGetPattern(out var text)) return new TextSnapshot(null, null);
            var document = Truncate(text.DocumentRange.GetText(20_000), 20_000);
            var selected = string.Join(string.Empty, text.GetSelection().Select(range => range.GetText(20_000)));
            return new TextSnapshot(document, string.IsNullOrEmpty(selected) ? null : Truncate(selected, 20_000));
        }
        catch
        {
            return new TextSnapshot(null, null);
        }
    }

    private static string StateText(ControlDescriptor control)
    {
        var states = new List<string>();
        if (control.Value is not null) states.Add($"value=\"{Escape(Truncate(control.Value, 160))}\"");
        if (control.IsReadOnly is not null) states.Add($"readonly={control.IsReadOnly.Value.ToString().ToLowerInvariant()}");
        if (control.IsSelected is not null) states.Add($"selected={control.IsSelected.Value.ToString().ToLowerInvariant()}");
        if (control.ToggleState is not null) states.Add($"toggle={control.ToggleState.ToLowerInvariant()}");
        if (control.ExpandCollapseState is not null) states.Add($"expand={control.ExpandCollapseState.ToLowerInvariant()}");
        return states.Count == 0 ? string.Empty : " " + string.Join(' ', states);
    }

    private static void Require(bool supported, string action)
    {
        if (!supported) throw new InvalidOperationException($"The selected control does not support the {action} UIA action.");
    }

    private static void Scroll(AutomationElement element, ScrollAmount horizontal, ScrollAmount vertical, string action)
    {
        Require(element.Patterns.Scroll.TryGetPattern(out var scroll), action);
        scroll!.Scroll(horizontal, vertical);
    }

    private static string Truncate(string? value, int maxLength)
    {
        value ??= string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static AutomationElement[] FindChildren(AutomationElement element)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { return element.FindAllChildren(); }
            catch when (attempt < 2) { Thread.Sleep(15 * (attempt + 1)); }
            catch { return []; }
        }
        return [];
    }

    private static LocatorSegment Segment(AutomationElement element, int ordinal)
    {
        var automationId = Safe(() => element.AutomationId ?? "", "");
        return new LocatorSegment(
            automationId,
            Safe(() => element.ControlType.ToString() ?? "Unknown", "Unknown"),
            automationId.Length == 0 ? Safe(() => element.Name ?? "", "") : string.Empty,
            Safe(() => element.ClassName ?? "", ""),
            ordinal);
    }

    private static bool SegmentMatches(AutomationElement element, LocatorSegment segment)
    {
        var candidate = Segment(element, 0);
        return string.Equals(candidate.AutomationId, segment.AutomationId, StringComparison.Ordinal) &&
               string.Equals(candidate.ControlType, segment.ControlType, StringComparison.Ordinal) &&
               string.Equals(candidate.Name, segment.Name, StringComparison.Ordinal) &&
               string.Equals(candidate.ClassName, segment.ClassName, StringComparison.Ordinal);
    }

    private static string SelectorKey(AutomationElement element)
    {
        var segment = Segment(element, 0);
        return string.Join('|', segment.AutomationId, segment.ControlType, segment.Name, segment.ClassName);
    }

    private static string ToSelector(LocatorSegment segment) => string.Join(';',
        $"automationId={Uri.EscapeDataString(segment.AutomationId)}",
        $"type={Uri.EscapeDataString(segment.ControlType)}",
        $"name={Uri.EscapeDataString(segment.Name)}",
        $"class={Uri.EscapeDataString(segment.ClassName)}",
        $"ordinal={segment.Ordinal}");

    private static string SelectorFor(long windowId, IEnumerable<LocatorSegment> path) =>
        $"window={windowId};path={string.Join('>', path.Select(ToSelector))}";

    private static string IdFor(long windowId, IEnumerable<LocatorSegment> path)
    {
        var selector = SelectorFor(windowId, path);
        return $"wc-{windowId:x}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selector)))[..16].ToLowerInvariant()}";
    }

    private static string Summary(AutomationElement element)
    {
        var bounds = Safe(() => element.BoundingRectangle.ToString(), "unavailable");
        return $"name={Safe(() => element.Name ?? "", "")};type={Safe(() => element.ControlType.ToString() ?? "", "")};bounds={bounds};enabled={Safe(() => element.IsEnabled, false)}";
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { return read(); }
            catch when (attempt < 2) { Thread.Sleep(10 * (attempt + 1)); }
            catch { return fallback; }
        }
        return fallback;
    }

    private static string Escape(string? value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

    private sealed record PendingElement(
        AutomationElement Element,
        int? ParentIndex,
        int Depth,
        IReadOnlyList<LocatorSegment> Path);

    private sealed record LocatorSegment(
        string AutomationId,
        string ControlType,
        string Name,
        string ClassName,
        int Ordinal);

    private sealed record ElementLocator(
        long WindowId,
        IReadOnlyList<LocatorSegment> Path,
        AutomationElement Element);

    private sealed record PatternState(
        string? Value,
        bool? IsReadOnly,
        bool? IsSelected,
        string? ToggleState,
        string? ExpandCollapseState,
        double? HorizontalScrollPercent,
        double? VerticalScrollPercent);

    private sealed record TextSnapshot(string? DocumentText, string? SelectedText);
}
