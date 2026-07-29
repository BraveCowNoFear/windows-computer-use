using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Broker;

public sealed class BrokerDispatcher : IDisposable
{
    private const int MaxScreenshotCacheEntries = 32;
    private const long MaxScreenshotCacheBase64Chars = 32L * 1024 * 1024;

    private readonly WindowService _windows = new();
    private readonly InputService _input = new();
    private readonly CaptureService _capture = new();
    private readonly DisplayService _displays = new();
    private readonly OcrService _ocr = new();
    private readonly ImageMatcherService _imageMatcher = new();
    private readonly VisualDiffService _visualDiff = new();
    private readonly ClipboardService _clipboard = new();
    private readonly UiaService _uia;
    private readonly AuditLogger _audit = new();
    private readonly string _sessionId = $"session-{Guid.NewGuid():N}";
    private readonly Dictionary<string, ScreenshotRecord> _screenshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WindowInspection> _observations = new(StringComparer.Ordinal);

    public BrokerDispatcher() => _uia = new UiaService(_windows, _input);

    public async Task<object?> DispatchAsync(string method, JsonElement args, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        using var uiLock = NeedsUiLock(method) ? UiControlLock.Acquire($"windows-computer-use:{_sessionId}:{method}") : null;
        try
        {
            var result = method switch
            {
                "health" => new
                {
                    ok = true,
                    session_id = _sessionId,
                    access_mode = "full-control",
                    backends = new[] { "uia3", "win32", "sendinput", "windows-graphics-capture", "print-window", "windows-media-ocr", "local-template-matching", "windows-ole-clipboard" },
                    dpi_awareness = "per-monitor-v2"
                },
                "list_windows" => new
                {
                    windows = _windows.ListWindows(args.Bool("include_untitled")),
                    coordinate_space = "physical-screen-pixels",
                    access_mode = "full-control"
                },
                "display_info" => _displays.GetTopology(),
                "pointer_position" => PointerPosition(),
                "window_from_point" => WindowFromPoint(args),
                "launch_app" => Launch(args),
                "wait_for_window" => await WaitForWindowAsync(args, cancellationToken),
                "inspect_window" => Inspect(args),
                "observe_changes" => ObserveChanges(args),
                "find_controls" => Find(args),
                "invoke" => Invoke(args),
                "perform_secondary_action" => PerformSecondaryAction(args),
                "enter_text" => EnterText(args),
                "paste_text" => PasteText(args),
                "copy_text" => CopyText(args),
                "wait_for_ui" => Wait(args),
                "wait_for_visual_change" => await WaitForVisualChangeAsync(args, cancellationToken),
                "wait_for_visual_stable" => await WaitForVisualStableAsync(args, cancellationToken),
                "compare_screenshots" => CompareScreenshots(args),
                "capture" => Capture(args),
                "capture_region" => CaptureRegion(args),
                "observe_desktop" => ObserveDesktop(args),
                "snapshot" => Snapshot(args),
                "ocr" => await OcrAsync(args, cancellationToken),
                "find_text" => await FindTextAsync(args, cancellationToken),
                "find_image" => FindImage(args),
                "read_clipboard_text" => _clipboard.ReadText(),
                "write_clipboard_text" => WriteClipboardText(args),
                "restore_clipboard" => RestoreClipboard(args),
                "recover_input_state" => RecoverInputState(args),
                "move_pointer" => MovePointer(args),
                "click" => Click(args),
                "mouse_down" => MouseDown(args),
                "mouse_up" => MouseUp(args),
                "press_key" => PressKey(args),
                "key_down" => KeyDown(args),
                "key_up" => KeyUp(args),
                "type_text" => TypeText(args),
                "scroll" => Scroll(args),
                "drag" => Drag(args),
                "set_window_state" => SetWindowState(args),
                "set_window_bounds" => SetWindowBounds(args),
                "activate_window" => Activate(args),
                "end_session" => EndSession(),
                _ => throw new NotSupportedException($"Unknown broker method: {method}")
            };
            _audit.Write(_sessionId, method, args, true, Environment.TickCount64 - started);
            return result;
        }
        catch (Exception error)
        {
            _audit.Write(_sessionId, method, args, false, Environment.TickCount64 - started, error.Message);
            throw;
        }
    }

    public void Dispose()
    {
        try { _input.ReleaseAllMouseButtons(); } catch { }
        try { _input.ReleaseAllKeys(); } catch { }
        _clipboard.Dispose();
        _uia.Dispose();
    }

    private static bool NeedsUiLock(string method) => method is
        "inspect_window" or "observe_changes" or "find_controls" or "invoke" or "perform_secondary_action" or "enter_text" or "paste_text" or "copy_text" or "wait_for_visual_change" or "wait_for_visual_stable" or "compare_screenshots" or "capture" or "capture_region" or "observe_desktop" or "snapshot" or "ocr" or "find_text" or "find_image" or "read_clipboard_text" or "write_clipboard_text" or "restore_clipboard" or "window_from_point" or
        "move_pointer" or "click" or "mouse_down" or "mouse_up" or "press_key" or "key_down" or "key_up" or "type_text" or "scroll" or "drag" or "set_window_state" or "set_window_bounds" or "activate_window" or "end_session" or "recover_input_state";

    private object Launch(JsonElement args)
    {
        var app = args.String("app") ?? throw new ArgumentException("app is required");
        var arguments = args.String("arguments") ?? string.Empty;
        var process = Process.Start(new ProcessStartInfo(app, arguments) { UseShellExecute = true })
            ?? throw new InvalidOperationException("Windows did not launch the requested app.");
        var timeout = Math.Clamp(args.Int("wait_ms", 1500), 0, 30_000);
        if (timeout > 0)
        {
            try { process.WaitForInputIdle(timeout); } catch { Thread.Sleep(Math.Min(timeout, 1000)); }
        }
        return new { ok = true, process_id = process.Id, app };
    }

    private object PointerPosition()
    {
        var point = _input.PointerPosition();
        return new { x = point.X, y = point.Y, coordinate_space = "physical-screen-pixels" };
    }

    private WindowHitTest WindowFromPoint(JsonElement args) => _windows.FromPoint(args.Int("x"), args.Int("y"));

    private object RecoverInputState(JsonElement args)
    {
        var keys = args.TryGetProperty("keys", out var keyValues) && keyValues.ValueKind == JsonValueKind.Array
            ? keyValues.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Reverse().ToArray()
            : [];
        var buttons = args.TryGetProperty("buttons", out var buttonValues) && buttonValues.ValueKind == JsonValueKind.Array
            ? buttonValues.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Reverse().ToArray()
            : [];
        var failures = new List<string>();
        var releasedKeys = 0;
        var releasedButtons = 0;
        foreach (var key in keys)
        {
            try { _input.KeyUp(key); releasedKeys++; }
            catch (Exception error) { failures.Add($"key {key}: {error.Message}"); }
        }
        var pointer = _input.PointerPosition();
        foreach (var button in buttons)
        {
            try { _input.MouseUp(pointer.X, pointer.Y, button); releasedButtons++; }
            catch (Exception error) { failures.Add($"button {button}: {error.Message}"); }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException($"Input recovery was incomplete: {string.Join("; ", failures)}");
        return new { ok = true, released_keys = releasedKeys, released_buttons = releasedButtons };
    }

    private async Task<object> WaitForWindowAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var title = args.String("title");
        var app = args.String("app");
        var windowClass = args.String("window_class");
        var processId = args.Int("process_id");
        var ownerId = args.Long("owner_window_id");
        var rootOwnerId = args.Long("root_owner_window_id");
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(app) &&
            string.IsNullOrWhiteSpace(windowClass) && processId == 0 && ownerId == 0 && rootOwnerId == 0)
            throw new ArgumentException("wait_for_window requires at least one window selector.");
        var state = args.String("state")?.ToLowerInvariant() ?? "exists";
        if (state is not "exists" and not "absent") throw new ArgumentException("state must be exists or absent");
        var timeoutMs = Math.Clamp(args.Int("timeout_ms", 10_000), 0, 120_000);
        var pollMs = Math.Clamp(args.Int("poll_ms", 100), 50, 5_000);
        var includeUntitled = args.Bool("include_untitled") || string.IsNullOrWhiteSpace(title);
        var started = Environment.TickCount64;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = _windows.ListWindows(includeUntitled).Where(window =>
                (title is null || window.Title?.Contains(title, StringComparison.OrdinalIgnoreCase) == true) &&
                (app is null || window.App.Contains(app, StringComparison.OrdinalIgnoreCase) ||
                    window.ProcessPath?.Contains(app, StringComparison.OrdinalIgnoreCase) == true) &&
                (windowClass is null || window.WindowClass.Contains(windowClass, StringComparison.OrdinalIgnoreCase)) &&
                (processId == 0 || window.ProcessId == processId) &&
                (ownerId == 0 || window.OwnerWindowId == ownerId) &&
                (rootOwnerId == 0 || window.RootOwnerWindowId == rootOwnerId)).ToArray();
            var matched = state == "exists" ? matches.Length > 0 : matches.Length == 0;
            if (matched || Environment.TickCount64 - started >= timeoutMs)
            {
                return new
                {
                    matched,
                    state,
                    elapsed_ms = Environment.TickCount64 - started,
                    count = matches.Length,
                    windows = matches
                };
            }
            await Task.Delay(pollMs, cancellationToken);
        }
    }

    private WindowInspection Inspect(JsonElement args)
    {
        var window = _windows.Resolve(args);
        return RememberObservation(_uia.Inspect(window, args.Int("limit", 400)));
    }

    private WindowDiff ObserveChanges(JsonElement args)
    {
        var previousId = args.String("previous_observation_id")
            ?? throw new ArgumentException("previous_observation_id is required");
        if (!_observations.TryGetValue(previousId, out var previous))
            throw new InvalidOperationException("Unknown or expired previous_observation_id. Inspect or snapshot the window again.");
        var window = _windows.Resolve(args);
        if (previous.Window.Id != window.Id)
            throw new InvalidOperationException("The previous observation belongs to a different window.");
        var current = RememberObservation(_uia.Inspect(window, args.Int("limit", 400)));
        var beforeById = previous.Controls.ToDictionary(control => control.Id, StringComparer.Ordinal);
        var afterById = current.Controls.ToDictionary(control => control.Id, StringComparer.Ordinal);
        var changes = new List<ControlChange>();
        foreach (var control in previous.Controls)
        {
            if (!afterById.TryGetValue(control.Id, out var after))
                changes.Add(new ControlChange("removed", control.Id, control, null));
            else if (!Equivalent(control, after))
                changes.Add(new ControlChange("changed", control.Id, control, after));
        }
        foreach (var control in current.Controls)
        {
            if (!beforeById.ContainsKey(control.Id))
                changes.Add(new ControlChange("added", control.Id, null, control));
        }
        return new WindowDiff(
            window,
            previousId,
            current.ObservationId,
            current.CapturedAt,
            changes,
            current.FocusedControlId,
            current.DocumentText,
            current.SelectedText,
            current.SelectedControlIds);
    }

    private object Find(JsonElement args)
    {
        var window = _windows.Resolve(args);
        var controls = _uia.Find(window, args, args.Int("limit", 50));
        return new { window, controls, count = controls.Count };
    }

    private ActionResult Invoke(JsonElement args)
    {
        var window = _windows.Activate(_windows.Resolve(args));
        try { return _uia.Invoke(window, args.String("control_id"), args); }
        finally { _screenshots.Clear(); }
    }

    private ActionResult EnterText(JsonElement args)
    {
        var text = args.String("text") ?? throw new ArgumentException("text is required");
        var window = _windows.Activate(_windows.Resolve(args));
        try { return _uia.EnterText(window, args.String("control_id"), args, text, args.Bool("append")); }
        finally { _screenshots.Clear(); }
    }

    private ActionResult PasteText(JsonElement args)
    {
        var text = args.String("text") ?? throw new ArgumentException("text is required");
        var append = args.Bool("append");
        var timeoutMs = Math.Clamp(args.Int("timeout_ms", 2_000), 100, 10_000);
        var settleMs = Math.Clamp(args.Int("settle_ms", 200), 50, 2_000);
        var window = _windows.Activate(_windows.Resolve(args));
        try
        {
            if (_input.HeldKeys.Count > 0)
                throw new InvalidOperationException($"paste_text requires all tracked keys to be released first; held: {string.Join(", ", _input.HeldKeys)}.");
            var beforeMatches = _uia.Find(window, args, 2);
            if (beforeMatches.Count != 1)
                throw new InvalidOperationException(beforeMatches.Count == 0
                    ? "No matching control was found for clipboard paste. Reinspect the window before retrying."
                    : "Clipboard paste selector is ambiguous. Use one stable control id.");
            var before = beforeMatches[0];
            var expected = before.Value is null || (append && before.Value.Length >= 4096)
                ? null
                : append ? before.Value + text : text;
            var expectedObserved = expected is { Length: > 4096 } ? expected[..4096] : expected;

            var after = _clipboard.UseTemporaryText(text, () =>
            {
                _ = _uia.PerformSecondaryAction(window, args.String("control_id"), args, "focus");
                _input.PressChord(append ? "ctrl+end" : "ctrl+a");
                _input.PressChord("ctrl+v");

                ControlDescriptor? observed = null;
                if (expectedObserved is null)
                {
                    Thread.Sleep(settleMs);
                    var matches = _uia.Find(window, args, 2);
                    if (matches.Count == 1) observed = matches[0];
                }
                else
                {
                    var deadline = Environment.TickCount64 + timeoutMs;
                    do
                    {
                        var matches = _uia.Find(window, args, 2, "value");
                        if (matches.Count == 1)
                        {
                            observed = matches[0];
                            if (string.Equals(observed.Value, expectedObserved, StringComparison.Ordinal)) break;
                        }
                        Thread.Sleep(25);
                    } while (Environment.TickCount64 < deadline);
                }

                if (observed is null)
                    throw new InvalidOperationException("The paste target could not be re-observed before restoring the clipboard.");
                if (expectedObserved is not null && !string.Equals(observed.Value, expectedObserved, StringComparison.Ordinal))
                    throw new InvalidOperationException("The paste target Value did not reach the expected text before the clipboard restore deadline.");
                return observed;
            });

            return new ActionResult(
                true,
                "paste_text",
                "windows-ole-clipboard+sendinput",
                new ActionVerification(
                    true,
                    expectedObserved is null ? "uia3-reobserve-and-clipboard-restore" : "uia3-value-and-clipboard-restore",
                    before.Value,
                    after.Value,
                    after.Id),
                new { control = after, clipboard_restored = true, append });
        }
        finally { _screenshots.Clear(); }
    }

    private ActionResult CopyText(JsonElement args)
    {
        var selection = args.String("selection")?.Trim().ToLowerInvariant() ?? "current";
        if (selection is not "current" and not "all") throw new ArgumentException("selection must be current or all");
        var timeoutMs = Math.Clamp(args.Int("timeout_ms", 2_000), 100, 10_000);
        var window = _windows.Activate(_windows.Resolve(args));
        try
        {
            if (_input.HeldKeys.Count > 0)
                throw new InvalidOperationException($"copy_text requires all tracked keys to be released first; held: {string.Join(", ", _input.HeldKeys)}.");
            var beforeMatches = _uia.Find(window, args, 2);
            if (beforeMatches.Count != 1)
                throw new InvalidOperationException(beforeMatches.Count == 0
                    ? "No matching control was found for clipboard copy. Reinspect the window before retrying."
                    : "Clipboard copy selector is ambiguous. Use one stable control id.");
            var before = beforeMatches[0];
            _ = _uia.PerformSecondaryAction(window, args.String("control_id"), args, "focus");
            if (selection == "all") _input.PressChord("ctrl+a");
            Thread.Sleep(40);
            var selectionObservation = _uia.Inspect(window, Math.Clamp(args.Int("scan_limit", 800), 1, 2_000));
            var expected = selectionObservation.FocusedControlId == before.Id ? selectionObservation.SelectedText : null;
            if (selection == "all" && expected is null && before.Value is { Length: < 4096 }) expected = before.Value;

            var copyAttempts = 1;
            ClipboardTextCapture captured;
            try
            {
                captured = _clipboard.CaptureText(() => _input.PressChord("ctrl+c"), Math.Max(100, timeoutMs / 2));
            }
            catch (TimeoutException firstTimeout)
            {
                copyAttempts++;
                _ = _uia.PerformSecondaryAction(window, args.String("control_id"), args, "focus");
                if (selection == "all") _input.PressChord("ctrl+a");
                Thread.Sleep(80);
                try
                {
                    captured = _clipboard.CaptureText(() => _input.PressChord("ctrl+c"), Math.Max(100, timeoutMs / 2));
                }
                catch (TimeoutException secondTimeout)
                {
                    throw new TimeoutException(
                        "The copy action did not change the clipboard after one semantic refocus retry.",
                        new AggregateException(firstTimeout, secondTimeout));
                }
            }
            if (copyAttempts == 1 && expected is { Length: < 20_000 } && !string.Equals(captured.Text, expected, StringComparison.Ordinal))
            {
                copyAttempts++;
                _ = _uia.PerformSecondaryAction(window, args.String("control_id"), args, "focus");
                if (selection == "all") _input.PressChord("ctrl+a");
                Thread.Sleep(80);
                captured = _clipboard.CaptureText(() => _input.PressChord("ctrl+c"), timeoutMs);
            }
            if (expected is { Length: < 20_000 } && !string.Equals(captured.Text, expected, StringComparison.Ordinal))
                throw new InvalidOperationException("Copied clipboard text did not equal the selected UIA text after one semantic refocus retry.");
            if (expected is { Length: 20_000 } && !captured.Text.StartsWith(expected, StringComparison.Ordinal))
                throw new InvalidOperationException("Copied clipboard text did not preserve the selected UIA text prefix.");

            var afterMatches = _uia.Find(window, args, 2);
            if (afterMatches.Count != 1) throw new InvalidOperationException("The copy target could not be re-observed after restoring the clipboard.");
            var after = afterMatches[0];
            return new ActionResult(
                true,
                "copy_text",
                "sendinput+windows-ole-clipboard",
                new ActionVerification(
                    true,
                    expected is null ? "clipboard-sequence-and-control-reobserve" : "clipboard-sequence-and-uia-selection",
                    expected,
                    captured.Text,
                    after.Id),
                new
                {
                    text = captured.Text,
                    length = captured.Text.Length,
                    sha256 = captured.Sha256,
                    normalized_sha256 = captured.NormalizedSha256,
                    formats = captured.Formats,
                    clipboard_restored = true,
                    copy_attempts = copyAttempts,
                    selection,
                    control = after
                });
        }
        finally { _screenshots.Clear(); }
    }

    private ActionResult PerformSecondaryAction(JsonElement args)
    {
        var action = args.String("action") ?? throw new ArgumentException("action is required");
        var window = _windows.Activate(_windows.Resolve(args));
        try { return _uia.PerformSecondaryAction(window, args.String("control_id"), args, action); }
        finally { _screenshots.Clear(); }
    }

    private object Wait(JsonElement args)
    {
        return _uia.WaitFor(() => _windows.Resolve(args), args, args.String("state") ?? "exists", args.Int("timeout_ms", 10_000), args.Int("poll_ms", 100));
    }

    private async Task<VisualChangeResult> WaitForVisualChangeAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var screenshotId = args.String("screenshot_id")
            ?? throw new ArgumentException("screenshot_id is required");
        if (!_screenshots.TryGetValue(screenshotId, out var previous))
            throw new InvalidOperationException("Unknown or expired screenshot_id. Capture or snapshot the same source again before waiting for a visual change.");
        ValidateScreenshotAge(args, previous);

        var timeout = Math.Clamp(args.Int("timeout_ms", 10_000), 100, 120_000);
        var poll = Math.Clamp(args.Int("poll_ms", 100), 25, 2_000);
        var started = Environment.TickCount64;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = ResolveVisualSource(previous);
            var capture = CaptureVisualSource(window, previous);
            var elapsed = Environment.TickCount64 - started;
            if (!string.Equals(capture.Sha256, previous.Sha256, StringComparison.OrdinalIgnoreCase) && elapsed <= timeout)
                return new VisualChangeResult(true, elapsed, screenshotId, previous.Sha256, RememberVisualCapture(window, capture, previous));
            if (elapsed >= timeout)
                return new VisualChangeResult(false, elapsed, screenshotId, previous.Sha256, RememberVisualCapture(window, capture, previous));

            await Task.Delay((int)Math.Min(poll, timeout - elapsed), cancellationToken);
        }
    }

    private async Task<VisualStabilityResult> WaitForVisualStableAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var screenshotId = args.String("screenshot_id")
            ?? throw new ArgumentException("screenshot_id is required");
        if (!_screenshots.TryGetValue(screenshotId, out var source))
            throw new InvalidOperationException("Unknown or expired screenshot_id. Capture or snapshot the source again before waiting for visual stability.");
        ValidateScreenshotAge(args, source);

        var timeout = Math.Clamp(args.Int("timeout_ms", 10_000), 100, 120_000);
        var stableTarget = Math.Clamp(args.Int("stable_ms", 500), 100, 10_000);
        var poll = Math.Clamp(args.Int("poll_ms", 100), 25, 2_000);
        var started = Environment.TickCount64;
        long stableSince = 0;
        string? candidateHash = null;
        var samples = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = ResolveVisualSource(source);
            var capture = CaptureVisualSource(window, source);
            samples++;
            var elapsed = Environment.TickCount64 - started;
            if (!string.Equals(capture.Sha256, candidateHash, StringComparison.OrdinalIgnoreCase))
            {
                candidateHash = capture.Sha256;
                stableSince = elapsed;
            }
            var stableFor = elapsed - stableSince;
            if (stableFor >= stableTarget && elapsed <= timeout)
                return new VisualStabilityResult(true, elapsed, stableFor, samples, screenshotId, RememberVisualCapture(window, capture, source));
            if (elapsed >= timeout)
                return new VisualStabilityResult(false, elapsed, stableFor, samples, screenshotId, RememberVisualCapture(window, capture, source));

            await Task.Delay((int)Math.Min(poll, timeout - elapsed), cancellationToken);
        }
    }

    private VisualDiffResult CompareScreenshots(JsonElement args)
    {
        var beforeId = args.String("before_screenshot_id")
            ?? throw new ArgumentException("before_screenshot_id is required");
        var afterId = args.String("after_screenshot_id")
            ?? throw new ArgumentException("after_screenshot_id is required");
        var before = ResolveCachedScreenshot(args, beforeId, "visual comparison", out _);
        var after = ResolveCachedScreenshot(args, afterId, "visual comparison", out _);
        if (before.WindowId != after.WindowId ||
            before.WindowBounds != after.WindowBounds ||
            before.SourceBounds != after.SourceBounds ||
            before.CaptureBounds != after.CaptureBounds ||
            before.ImageRegion != after.ImageRegion)
            throw new InvalidOperationException("Screenshots must belong to the same window or desktop source and the same image region for visual comparison.");
        return _visualDiff.Compare(
            before.Capture,
            after.Capture,
            args.Int("channel_threshold"),
            args.Int("tile_size", 32),
            args.Int("max_regions", 50));
    }

    private WindowDescriptor? ResolveVisualSource(ScreenshotRecord source)
    {
        if (source.WindowId is long windowId)
        {
            var window = _windows.Resolve(windowId);
            if (window.Id != windowId)
                throw new InvalidOperationException("The source window was recreated after the screenshot. Capture or snapshot it again before waiting on visual content.");
            if (window.Bounds != source.WindowBounds)
                throw new InvalidOperationException("The source window moved or resized after the screenshot. Capture or snapshot it again before waiting on visual content.");
            return window;
        }
        if (source.SourceBounds != VirtualDesktopBounds())
            throw new InvalidOperationException("The virtual desktop topology changed after the screenshot. Capture it again before waiting on visual content.");
        return null;
    }

    private CaptureResult CaptureVisualSource(WindowDescriptor? window, ScreenshotRecord source)
    {
        var full = _capture.Capture(window);
        if (full.Bounds != source.SourceBounds)
            throw new InvalidOperationException("The visual source bounds changed after the screenshot. Capture the intended region again before waiting on visual content.");
        return source.ImageRegion is null ? full : _capture.Crop(full, source.ImageRegion);
    }

    private CaptureResult RememberVisualCapture(WindowDescriptor? window, CaptureResult capture, ScreenshotRecord source) =>
        RememberCapture(window, capture, source.SourceBounds, source.ImageRegion);

    private CaptureResult Capture(JsonElement args)
    {
        var desktop = args.Bool("desktop");
        if (desktop && HasWindowSelector(args)) throw new ArgumentException("desktop=true cannot be combined with a window selector.");
        var window = desktop ? null : _windows.Resolve(args);
        return RememberCapture(window, _capture.Capture(window, args.String("path")));
    }

    private CaptureResult CaptureRegion(JsonElement args)
    {
        var screenshotId = args.String("screenshot_id");
        if (!string.IsNullOrWhiteSpace(screenshotId))
        {
            var source = ResolveCachedScreenshot(args, "cropping", out var cachedWindow);
            var cachedRegion = CaptureRegionRectangle(args);
            var cachedCrop = _capture.Crop(source.Capture, cachedRegion, args.String("path"));
            var combinedRegion = source.ImageRegion is null
                ? cachedRegion
                : new RectDto(
                    source.ImageRegion.X + cachedRegion.X,
                    source.ImageRegion.Y + cachedRegion.Y,
                    cachedRegion.Width,
                    cachedRegion.Height);
            return RememberCapture(cachedWindow, cachedCrop, source.SourceBounds, combinedRegion);
        }

        var desktop = args.Bool("desktop");
        if (desktop && HasWindowSelector(args)) throw new ArgumentException("desktop=true cannot be combined with a window selector.");
        var window = desktop ? null : _windows.Resolve(args);
        var full = _capture.Capture(window);
        var region = CaptureRegionRectangle(args);
        var cropped = _capture.Crop(full, region, args.String("path"));
        return RememberCapture(window, cropped, full.Bounds, region);
    }

    private static RectDto CaptureRegionRectangle(JsonElement args) =>
        new(args.Int("x"), args.Int("y"), args.Int("width"), args.Int("height"));

    private DesktopStateSnapshot ObserveDesktop(JsonElement args)
    {
        if (HasWindowSelector(args)) throw new ArgumentException("observe_desktop cannot be combined with a window selector.");
        var capture = RememberCapture(null, _capture.Capture(null, args.String("path")));
        var pointer = _input.PointerPosition();
        return new DesktopStateSnapshot(
            _displays.GetTopology(),
            _windows.ListWindows(args.Bool("include_untitled")),
            new PointerDescriptor(pointer.X, pointer.Y),
            capture);
    }

    private WindowStateSnapshot Snapshot(JsonElement args)
    {
        var window = _windows.Resolve(args);
        var inspection = RememberObservation(_uia.Inspect(window, args.Int("limit", 400)));
        var capture = RememberCapture(window, _capture.Capture(window, args.String("path")));
        return new WindowStateSnapshot(inspection, capture);
    }

    private async Task<object> OcrAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var screenshotId = args.String("screenshot_id");
        var suppliedPath = args.String("path");
        if (!string.IsNullOrWhiteSpace(screenshotId) && !string.IsNullOrWhiteSpace(suppliedPath))
            throw new ArgumentException("screenshot_id cannot be combined with path. The cached screenshot is the authoritative OCR source.");
        if (!string.IsNullOrWhiteSpace(suppliedPath) && (args.Bool("desktop") || HasWindowSelector(args)))
            throw new ArgumentException("path cannot be combined with desktop=true or a window selector. The existing image is the authoritative OCR source.");

        var temporary = string.IsNullOrWhiteSpace(suppliedPath);
        var path = suppliedPath;
        CaptureResult? capture = null;
        WindowDescriptor? capturedWindow = null;
        try
        {
            if (temporary)
            {
                path = Path.Combine(Path.GetTempPath(), $"wcu-ocr-{Guid.NewGuid():N}.png");
                if (!string.IsNullOrWhiteSpace(screenshotId))
                {
                    capture = ResolveCachedScreenshot(args, "OCR", out _).Capture;
                    WriteCapture(path, capture);
                }
                else
                {
                    if (args.Bool("desktop") && HasWindowSelector(args)) throw new ArgumentException("desktop=true cannot be combined with a window selector.");
                    capturedWindow = args.Bool("desktop") ? null : _windows.Resolve(args);
                    capture = RememberCapture(capturedWindow, _capture.Capture(capturedWindow, path));
                }
            }
            var node = ToJsonObject(await _ocr.RecognizeAsync(path!, args.String("language"), cancellationToken));
            if (capture is not null) AddCaptureMetadata(node, capture);
            return node;
        }
        finally { if (temporary && File.Exists(path)) File.Delete(path); }
    }

    private async Task<object> FindTextAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var query = args.String("text") ?? throw new ArgumentException("text is required");
        var mode = args.String("match")?.ToLowerInvariant() ?? "contains";
        if (mode is not "exact" and not "contains") throw new ArgumentException("match must be exact or contains");
        var comparison = args.Bool("case_sensitive") ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var limit = Math.Clamp(args.Int("limit", 50), 1, 200);
        var path = Path.Combine(Path.GetTempPath(), $"wcu-find-text-{Guid.NewGuid():N}.png");
        try
        {
            CaptureResult capture;
            var screenshotId = args.String("screenshot_id");
            if (!string.IsNullOrWhiteSpace(screenshotId))
            {
                capture = ResolveCachedScreenshot(args, "text recognition", out _).Capture;
                WriteCapture(path, capture);
            }
            else
            {
                if (args.Bool("desktop") && HasWindowSelector(args)) throw new ArgumentException("desktop=true cannot be combined with a window selector.");
                var window = args.Bool("desktop") ? null : _windows.Resolve(args);
                capture = RememberCapture(window, _capture.Capture(window, path));
            }
            var ocr = ToJsonObject(await _ocr.RecognizeAsync(path, args.String("language"), cancellationToken));
            if (ocr["ok"]?.GetValue<bool>() != true)
                throw new InvalidOperationException($"Windows OCR failed: {ocr["error"]?.GetValue<string>()}");
            var matches = new JsonArray();
            if (ocr["lines"] is JsonArray lines)
            {
                foreach (var lineNode in lines.OfType<JsonObject>())
                {
                    AddOcrMatch(matches, lineNode, "line", query, mode, comparison, capture, limit);
                    if (lineNode["words"] is not JsonArray words) continue;
                    foreach (var wordNode in words.OfType<JsonObject>())
                    {
                        AddOcrMatch(matches, wordNode, "word", query, mode, comparison, capture, limit);
                        if (matches.Count >= limit) break;
                    }
                    if (matches.Count >= limit) break;
                }
            }
            return new JsonObject
            {
                ["ok"] = true,
                ["backend"] = "windows-media-ocr",
                ["recognized_text"] = ocr["text"]?.DeepClone(),
                ["query"] = query,
                ["match"] = mode,
                ["screenshot_id"] = capture.Id,
                ["captured_at"] = capture.CapturedAt,
                ["capture_bounds"] = JsonSerializer.SerializeToNode(capture.Bounds, ProtocolJson.Options),
                ["coordinate_space"] = "screenshot",
                ["matches"] = matches,
                ["count"] = matches.Count
            };
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private object FindImage(JsonElement args)
    {
        var templatePath = args.String("template_path") ?? throw new ArgumentException("template_path is required");
        CaptureResult capture;
        if (!string.IsNullOrWhiteSpace(args.String("screenshot_id")))
        {
            capture = ResolveCachedScreenshot(args, "image matching", out _).Capture;
        }
        else
        {
            if (args.Bool("desktop") && HasWindowSelector(args)) throw new ArgumentException("desktop=true cannot be combined with a window selector.");
            var window = args.Bool("desktop") ? null : _windows.Resolve(args);
            capture = RememberCapture(window, _capture.Capture(window));
        }
        return _imageMatcher.Find(
            templatePath,
            capture,
            args.Double("threshold", 0.92),
            args.Int("max_results", 10),
            args.Double("scale_min", 1.0),
            args.Double("scale_max", 1.0),
            args.Double("scale_step", 0.1));
    }

    private object WriteClipboardText(JsonElement args)
    {
        var text = args.String("text") ?? throw new ArgumentException("text is required");
        return _clipboard.WriteText(text, args.Bool("preserve_previous", true));
    }

    private object RestoreClipboard(JsonElement args)
    {
        var backupId = args.String("backup_id") ?? throw new ArgumentException("backup_id is required");
        return _clipboard.Restore(backupId);
    }

    private object MovePointer(JsonElement args)
    {
        var coordinateSpace = args.String("coordinate_space")?.ToLowerInvariant() ?? "screen";
        if (coordinateSpace is not "window" and not "screen" and not "screenshot")
            throw new ArgumentException("coordinate_space must be window, screen, or screenshot");
        var screenshotId = args.String("screenshot_id");
        var hasWindowSelector = HasWindowSelector(args);
        WindowDescriptor? window = null;
        if (hasWindowSelector)
        {
            window = _windows.Resolve(args);
        }
        else if (!string.IsNullOrWhiteSpace(screenshotId))
        {
            if (!_screenshots.TryGetValue(screenshotId, out var cached))
                throw new InvalidOperationException("Unknown or expired screenshot_id. Capture or snapshot the target window again.");
            if (cached.WindowId is long windowId) window = _windows.Resolve(windowId);
        }
        else if (coordinateSpace != "screen")
        {
            throw new ArgumentException($"coordinate_space={coordinateSpace} requires a window selector or screenshot_id.");
        }

        var screenshot = window is null ? ValidateDesktopScreenshot(args) : ValidateScreenshot(args, window);
        var requested = coordinateSpace switch
        {
            "screen" => (args.Int("x"), args.Int("y")),
            "window" => _input.WindowPoint(window!, args.Int("x"), args.Int("y"), true),
            _ => ScreenshotPoint(screenshot, args.Int("x"), args.Int("y"))
        };
        _input.MovePointer(requested.Item1, requested.Item2, args.Int("duration_ms"));
        var actual = _input.PointerPosition();
        if (actual != requested)
            throw new InvalidOperationException($"Windows placed the pointer at {actual.X},{actual.Y} instead of {requested.Item1},{requested.Item2}.");
        _screenshots.Clear();
        Thread.Sleep(100);
        var afterWindow = window is null ? null : _windows.Resolve(window.Id);
        var afterCapture = RememberCapture(afterWindow, _capture.Capture(afterWindow));
        return new
        {
            ok = true,
            backend = "win32-set-cursor-pos",
            coordinate_space = coordinateSpace,
            screen_position = new { x = actual.X, y = actual.Y },
            duration_ms = Math.Clamp(args.Int("duration_ms"), 0, 10_000),
            after_screenshot_id = afterCapture.Id,
            visual_changed = screenshot is null ? (bool?)null : screenshot.Sha256 != afterCapture.Sha256,
            visual_diff = ActionVisualDiff(screenshot, afterCapture)
        };
    }

    private ActionResult Click(JsonElement args)
    {
        var desktopCapture = ValidateDesktopScreenshot(args);
        if (desktopCapture is not null)
        {
            var desktopPoint = ScreenshotPoint(desktopCapture, args.Int("x"), args.Int("y"));
            _input.Click(desktopPoint.X, desktopPoint.Y, args.String("button") ?? "left", args.Int("count", 1));
            _screenshots.Clear();
            Thread.Sleep(100);
            var desktopAfterCapture = RememberCapture(null, _capture.Capture(null));
            return new ActionResult(
                true,
                "click",
                "sendinput",
                new ActionVerification(true, "desktop-screenshot-reobserve", desktopCapture.Sha256, desktopAfterCapture.Sha256),
                new
                {
                    x = desktopPoint.X,
                    y = desktopPoint.Y,
                    coordinate_space = "physical-screen-pixels",
                    after_screenshot_id = desktopAfterCapture.Id,
                    visual_changed = desktopCapture.Sha256 != desktopAfterCapture.Sha256,
                    visual_diff = ActionVisualDiff(desktopCapture, desktopAfterCapture)
                });
        }
        if (IsDirectScreenAction(args))
        {
            var screenPoint = (X: args.Int("x"), Y: args.Int("y"));
            _input.Click(screenPoint.X, screenPoint.Y, args.String("button") ?? "left", args.Int("count", 1));
            _screenshots.Clear();
            Thread.Sleep(100);
            var screenAfterCapture = RememberCapture(null, _capture.Capture(null));
            return new ActionResult(
                true,
                "click",
                "sendinput",
                new ActionVerification(true, "screen-input-and-desktop-reobserve", null, screenAfterCapture.Sha256),
                new { x = screenPoint.X, y = screenPoint.Y, coordinate_space = "physical-screen-pixels", after_screenshot_id = screenAfterCapture.Id });
        }
        var resolved = _windows.Resolve(args);
        var beforeCapture = ValidateScreenshot(args, resolved);
        var window = _windows.Activate(resolved);
        var point = ResolvePoint(args, window, beforeCapture, "x", "y");
        _input.Click(point.X, point.Y, args.String("button") ?? "left", args.Int("count", 1));
        _screenshots.Clear();
        Thread.Sleep(100);
        var after = _windows.Resolve(window.Id);
        var afterCapture = RememberCapture(after, _capture.Capture(after));
        return new ActionResult(
            true,
            "click",
            "sendinput",
            new ActionVerification(
                after.IsForeground,
                "window-and-screenshot-reobserve",
                beforeCapture?.Sha256,
                afterCapture.Sha256),
            new
            {
                x = point.X,
                y = point.Y,
                after_screenshot_id = afterCapture.Id,
                visual_changed = beforeCapture is null ? (bool?)null : beforeCapture.Sha256 != afterCapture.Sha256,
                visual_diff = ActionVisualDiff(beforeCapture, afterCapture)
            });
    }

    private ActionResult MouseDown(JsonElement args)
    {
        var desktopCapture = ValidateDesktopScreenshot(args);
        if (desktopCapture is not null)
        {
            var desktopPoint = ScreenshotPoint(desktopCapture, args.Int("x"), args.Int("y"));
            var desktopButton = InputService.PlanMouseButton(args.String("button") ?? "left").Name;
            var desktopBefore = string.Join('+', _input.HeldMouseButtons);
            _input.MouseDown(desktopPoint.X, desktopPoint.Y, desktopButton);
            _screenshots.Clear();
            Thread.Sleep(100);
            var desktopAfterCapture = RememberCapture(null, _capture.Capture(null));
            var desktopAfter = string.Join('+', _input.HeldMouseButtons);
            return new ActionResult(
                true,
                "mouse_down",
                "sendinput-mouse-state",
                new ActionVerification(_input.HeldMouseButtons.Contains(desktopButton), "held-mouse-state", desktopBefore, desktopAfter),
                new
                {
                    x = desktopPoint.X,
                    y = desktopPoint.Y,
                    button = desktopButton,
                    held_buttons = _input.HeldMouseButtons,
                    desktop = true,
                    after_screenshot_id = desktopAfterCapture.Id,
                    visual_changed = desktopCapture.Sha256 != desktopAfterCapture.Sha256,
                    visual_diff = ActionVisualDiff(desktopCapture, desktopAfterCapture)
                });
        }
        if (IsDirectScreenAction(args))
        {
            var screenPoint = (X: args.Int("x"), Y: args.Int("y"));
            var screenButton = InputService.PlanMouseButton(args.String("button") ?? "left").Name;
            var screenBefore = string.Join('+', _input.HeldMouseButtons);
            _input.MouseDown(screenPoint.X, screenPoint.Y, screenButton);
            _screenshots.Clear();
            Thread.Sleep(100);
            var screenAfterCapture = RememberCapture(null, _capture.Capture(null));
            var screenAfter = string.Join('+', _input.HeldMouseButtons);
            return new ActionResult(
                true,
                "mouse_down",
                "sendinput-mouse-state",
                new ActionVerification(_input.HeldMouseButtons.Contains(screenButton), "held-mouse-state", screenBefore, screenAfter),
                new
                {
                    x = screenPoint.X,
                    y = screenPoint.Y,
                    button = screenButton,
                    held_buttons = _input.HeldMouseButtons,
                    desktop = true,
                    after_screenshot_id = screenAfterCapture.Id
                });
        }
        var resolved = _windows.Resolve(args);
        var beforeCapture = ValidateScreenshot(args, resolved);
        var window = _windows.Activate(resolved);
        var point = ResolvePoint(args, window, beforeCapture, "x", "y");
        var button = InputService.PlanMouseButton(args.String("button") ?? "left").Name;
        var before = string.Join('+', _input.HeldMouseButtons);
        _input.MouseDown(point.X, point.Y, button);
        _screenshots.Clear();
        Thread.Sleep(100);
        var afterWindow = _windows.Resolve(window.Id);
        var afterCapture = RememberCapture(afterWindow, _capture.Capture(afterWindow));
        var after = string.Join('+', _input.HeldMouseButtons);
        return new ActionResult(
            true,
            "mouse_down",
            "sendinput-mouse-state",
            new ActionVerification(_input.HeldMouseButtons.Contains(button), "held-mouse-state", before, after),
            new
            {
                x = point.X,
                y = point.Y,
                button,
                held_buttons = _input.HeldMouseButtons,
                window_id = window.Id,
                after_screenshot_id = afterCapture.Id,
                visual_changed = beforeCapture is null ? (bool?)null : beforeCapture.Sha256 != afterCapture.Sha256,
                visual_diff = ActionVisualDiff(beforeCapture, afterCapture)
            });
    }

    private ActionResult MouseUp(JsonElement args)
    {
        var desktopCapture = ValidateDesktopScreenshot(args);
        if (desktopCapture is not null)
        {
            var desktopPoint = ScreenshotPoint(desktopCapture, args.Int("x"), args.Int("y"));
            var desktopButton = InputService.PlanMouseButton(args.String("button") ?? "left").Name;
            var desktopBefore = string.Join('+', _input.HeldMouseButtons);
            _input.MouseUp(desktopPoint.X, desktopPoint.Y, desktopButton);
            _screenshots.Clear();
            Thread.Sleep(100);
            var desktopAfterCapture = RememberCapture(null, _capture.Capture(null));
            var desktopAfter = string.Join('+', _input.HeldMouseButtons);
            return new ActionResult(
                true,
                "mouse_up",
                "sendinput-mouse-state",
                new ActionVerification(!_input.HeldMouseButtons.Contains(desktopButton), "held-mouse-state", desktopBefore, desktopAfter),
                new
                {
                    x = desktopPoint.X,
                    y = desktopPoint.Y,
                    button = desktopButton,
                    held_buttons = _input.HeldMouseButtons,
                    desktop = true,
                    after_screenshot_id = desktopAfterCapture.Id,
                    visual_changed = desktopCapture.Sha256 != desktopAfterCapture.Sha256,
                    visual_diff = ActionVisualDiff(desktopCapture, desktopAfterCapture)
                });
        }
        if (IsDirectScreenAction(args))
        {
            var screenPoint = (X: args.Int("x"), Y: args.Int("y"));
            var screenButton = InputService.PlanMouseButton(args.String("button") ?? "left").Name;
            var screenBefore = string.Join('+', _input.HeldMouseButtons);
            _input.MouseUp(screenPoint.X, screenPoint.Y, screenButton);
            _screenshots.Clear();
            Thread.Sleep(100);
            var screenAfterCapture = RememberCapture(null, _capture.Capture(null));
            var screenAfter = string.Join('+', _input.HeldMouseButtons);
            return new ActionResult(
                true,
                "mouse_up",
                "sendinput-mouse-state",
                new ActionVerification(!_input.HeldMouseButtons.Contains(screenButton), "held-mouse-state", screenBefore, screenAfter),
                new
                {
                    x = screenPoint.X,
                    y = screenPoint.Y,
                    button = screenButton,
                    held_buttons = _input.HeldMouseButtons,
                    desktop = true,
                    after_screenshot_id = screenAfterCapture.Id
                });
        }
        var resolved = _windows.Resolve(args);
        var beforeCapture = ValidateScreenshot(args, resolved);
        var window = _windows.Activate(resolved);
        var point = ResolvePoint(args, window, beforeCapture, "x", "y");
        var button = InputService.PlanMouseButton(args.String("button") ?? "left").Name;
        var before = string.Join('+', _input.HeldMouseButtons);
        _input.MouseUp(point.X, point.Y, button);
        _screenshots.Clear();
        Thread.Sleep(100);
        var afterWindow = _windows.Resolve(window.Id);
        var afterCapture = RememberCapture(afterWindow, _capture.Capture(afterWindow));
        var after = string.Join('+', _input.HeldMouseButtons);
        return new ActionResult(
            true,
            "mouse_up",
            "sendinput-mouse-state",
            new ActionVerification(!_input.HeldMouseButtons.Contains(button), "held-mouse-state", before, after),
            new
            {
                x = point.X,
                y = point.Y,
                button,
                held_buttons = _input.HeldMouseButtons,
                window_id = window.Id,
                after_screenshot_id = afterCapture.Id,
                visual_changed = beforeCapture is null ? (bool?)null : beforeCapture.Sha256 != afterCapture.Sha256,
                visual_diff = ActionVisualDiff(beforeCapture, afterCapture)
            });
    }

    private ActionResult PressKey(JsonElement args)
    {
        var key = args.String("key") ?? throw new ArgumentException("key is required");
        var target = ResolveKeyboardTarget(args);
        var beforeCapture = CaptureKeyboardBaseline(target);
        var repeat = Math.Clamp(args.Int("repeat", 1), 1, 100);
        var intervalMs = Math.Clamp(args.Int("interval_ms", 40), 0, 5_000);
        for (var index = 0; index < repeat; index++)
        {
            _input.PressChord(key);
            if (index + 1 < repeat && intervalMs > 0) Thread.Sleep(intervalMs);
        }
        _screenshots.Clear();
        var (after, afterCapture) = ReobserveKeyboardTarget(target);
        if (target.Desktop)
        {
            return new ActionResult(
                true,
                "press_key",
                "sendinput-current-foreground",
                new ActionVerification(true, "foreground-input-no-activation", target.Window.Id.ToString(), after?.Id.ToString()),
                new
                {
                    repeat,
                    interval_ms = intervalMs,
                    desktop = true,
                    foreground_before = target.Window,
                    foreground_after = after,
                    after_screenshot_id = afterCapture.Id,
                    visual_changed = beforeCapture.Sha256 != afterCapture.Sha256,
                    visual_diff = ActionVisualDiff(beforeCapture, afterCapture)
                });
        }
        return new ActionResult(
            true,
            "press_key",
            "sendinput",
            new ActionVerification(true, "foreground-window", target.Window.Id.ToString(), after!.IsForeground.ToString()),
            new
            {
                repeat,
                interval_ms = intervalMs,
                desktop = false,
                after_screenshot_id = afterCapture.Id,
                visual_changed = beforeCapture.Sha256 != afterCapture.Sha256,
                visual_diff = ActionVisualDiff(beforeCapture, afterCapture)
            });
    }

    private ActionResult KeyDown(JsonElement args)
    {
        var key = args.String("key") ?? throw new ArgumentException("key is required");
        var target = ResolveKeyboardTarget(args);
        var beforeCapture = CaptureKeyboardBaseline(target);
        _input.KeyDown(key);
        _screenshots.Clear();
        var (_, afterCapture) = ReobserveKeyboardTarget(target);
        return new ActionResult(
            true,
            "key_down",
            target.Desktop ? "sendinput-current-foreground-key-state" : "sendinput-key-state",
            new ActionVerification(true, "held-key-state", null, string.Join('+', _input.HeldKeys)),
            new
            {
                held_keys = _input.HeldKeys,
                window_id = target.Window.Id,
                desktop = target.Desktop,
                after_screenshot_id = afterCapture.Id,
                visual_changed = beforeCapture.Sha256 != afterCapture.Sha256,
                visual_diff = ActionVisualDiff(beforeCapture, afterCapture)
            });
    }

    private ActionResult KeyUp(JsonElement args)
    {
        var key = args.String("key") ?? throw new ArgumentException("key is required");
        var desktop = args.Bool("desktop");
        if (desktop && HasWindowSelector(args)) throw new ArgumentException("desktop=true cannot be combined with a window selector.");
        var targetWindow = desktop ? _windows.GetForeground() : _windows.Activate(_windows.Resolve(args));
        var baselineSource = desktop ? null : targetWindow;
        var baselineCapture = RememberCapture(baselineSource, _capture.Capture(baselineSource));
        var beforeCapture = _screenshots[baselineCapture.Id];
        var before = string.Join('+', _input.HeldKeys);
        _input.KeyUp(key);
        _screenshots.Clear();
        Thread.Sleep(100);
        var after = desktop ? _windows.GetForeground() : _windows.Resolve(targetWindow!.Id);
        var afterSource = desktop ? null : after;
        var afterCapture = RememberCapture(afterSource, _capture.Capture(afterSource));
        return new ActionResult(
            true,
            "key_up",
            desktop ? "sendinput-current-foreground-key-state" : "sendinput-key-state",
            new ActionVerification(true, "held-key-state", before, string.Join('+', _input.HeldKeys)),
            new
            {
                held_keys = _input.HeldKeys,
                window_id = after?.Id ?? targetWindow?.Id,
                desktop,
                after_screenshot_id = afterCapture.Id,
                visual_changed = beforeCapture.Sha256 != afterCapture.Sha256,
                visual_diff = ActionVisualDiff(beforeCapture, afterCapture)
            });
    }

    private ActionResult TypeText(JsonElement args)
    {
        var text = args.String("text") ?? throw new ArgumentException("text is required");
        var target = ResolveKeyboardTarget(args);
        var beforeCapture = CaptureKeyboardBaseline(target);
        _input.TypeText(text);
        _screenshots.Clear();
        var (after, afterCapture) = ReobserveKeyboardTarget(target);
        if (target.Desktop)
        {
            return new ActionResult(
                true,
                "type_text",
                "sendinput-unicode-current-foreground",
                new ActionVerification(true, "foreground-input-no-activation", target.Window.Id.ToString(), after?.Id.ToString()),
                new
                {
                    desktop = true,
                    foreground_before = target.Window,
                    foreground_after = after,
                    after_screenshot_id = afterCapture.Id,
                    visual_changed = beforeCapture.Sha256 != afterCapture.Sha256,
                    visual_diff = ActionVisualDiff(beforeCapture, afterCapture)
                });
        }
        return new ActionResult(
            true,
            "type_text",
            "sendinput-unicode",
            new ActionVerification(true, "foreground-window", target.Window.Id.ToString(), after!.IsForeground.ToString()),
            new
            {
                desktop = false,
                after_screenshot_id = afterCapture.Id,
                visual_changed = beforeCapture.Sha256 != afterCapture.Sha256,
                visual_diff = ActionVisualDiff(beforeCapture, afterCapture)
            });
    }

    private (WindowDescriptor Window, bool Desktop) ResolveKeyboardTarget(JsonElement args)
    {
        if (!args.Bool("desktop")) return (_windows.Activate(_windows.Resolve(args)), false);
        if (HasWindowSelector(args)) throw new ArgumentException("desktop=true cannot be combined with a window selector.");
        var foreground = _windows.GetForeground()
            ?? throw new InvalidOperationException("Windows has no current foreground window. Select and activate a window before sending desktop keyboard input.");
        return (foreground, true);
    }

    private ScreenshotRecord CaptureKeyboardBaseline((WindowDescriptor Window, bool Desktop) target)
    {
        var source = target.Desktop ? null : target.Window;
        var capture = RememberCapture(source, _capture.Capture(source));
        return _screenshots[capture.Id];
    }

    private (WindowDescriptor? Window, CaptureResult Capture) ReobserveKeyboardTarget((WindowDescriptor Window, bool Desktop) target)
    {
        Thread.Sleep(100);
        if (target.Desktop)
        {
            var foreground = _windows.GetForeground();
            return (foreground, RememberCapture(null, _capture.Capture(null)));
        }

        var window = _windows.Resolve(target.Window.Id);
        return (window, RememberCapture(window, _capture.Capture(window)));
    }

    private ActionResult Scroll(JsonElement args)
    {
        var desktopCapture = ValidateDesktopScreenshot(args);
        if (desktopCapture is not null)
        {
            var desktopPoint = ScreenshotPoint(
                desktopCapture,
                args.Int("x", desktopCapture.CaptureBounds.Width / 2),
                args.Int("y", desktopCapture.CaptureBounds.Height / 2));
            _input.Scroll(desktopPoint.X, desktopPoint.Y, args.Int("vertical"), args.Int("horizontal"));
            _screenshots.Clear();
            Thread.Sleep(100);
            var desktopAfterCapture = RememberCapture(null, _capture.Capture(null));
            return new ActionResult(
                true,
                "scroll",
                "sendinput",
                new ActionVerification(true, "desktop-screenshot-reobserve", desktopCapture.Sha256, desktopAfterCapture.Sha256),
                new
                {
                    x = desktopPoint.X,
                    y = desktopPoint.Y,
                    after_screenshot_id = desktopAfterCapture.Id,
                    visual_changed = desktopCapture.Sha256 != desktopAfterCapture.Sha256,
                    visual_diff = ActionVisualDiff(desktopCapture, desktopAfterCapture)
                });
        }
        if (IsDirectScreenAction(args))
        {
            var bounds = VirtualDesktopBounds();
            var screenPoint = (X: args.Int("x", bounds.X + bounds.Width / 2), Y: args.Int("y", bounds.Y + bounds.Height / 2));
            _input.Scroll(screenPoint.X, screenPoint.Y, args.Int("vertical"), args.Int("horizontal"));
            _screenshots.Clear();
            Thread.Sleep(100);
            var screenAfterCapture = RememberCapture(null, _capture.Capture(null));
            return new ActionResult(
                true,
                "scroll",
                "sendinput",
                new ActionVerification(true, "screen-input-and-desktop-reobserve", null, screenAfterCapture.Sha256),
                new { x = screenPoint.X, y = screenPoint.Y, after_screenshot_id = screenAfterCapture.Id });
        }
        var resolved = _windows.Resolve(args);
        var beforeCapture = ValidateScreenshot(args, resolved);
        var window = _windows.Activate(resolved);
        var point = ResolvePoint(args, window, beforeCapture, "x", "y", window.Bounds.Width / 2, window.Bounds.Height / 2);
        _input.Scroll(point.X, point.Y, args.Int("vertical"), args.Int("horizontal"));
        _screenshots.Clear();
        Thread.Sleep(100);
        var after = _windows.Resolve(window.Id);
        var afterCapture = RememberCapture(after, _capture.Capture(after));
        return new ActionResult(
            true,
            "scroll",
            "sendinput",
            new ActionVerification(after.IsForeground, "window-and-screenshot-reobserve", beforeCapture?.Sha256, afterCapture.Sha256),
            new
            {
                x = point.X,
                y = point.Y,
                after_screenshot_id = afterCapture.Id,
                visual_changed = beforeCapture is null ? (bool?)null : beforeCapture.Sha256 != afterCapture.Sha256,
                visual_diff = ActionVisualDiff(beforeCapture, afterCapture)
            });
    }

    private ActionResult Drag(JsonElement args)
    {
        var desktopCapture = ValidateDesktopScreenshot(args);
        if (desktopCapture is not null)
        {
            var desktopFrom = ScreenshotPoint(desktopCapture, args.Int("from_x"), args.Int("from_y"));
            var desktopTo = ScreenshotPoint(desktopCapture, args.Int("to_x"), args.Int("to_y"));
            var desktopButton = InputService.PlanMouseButton(args.String("button") ?? "left").Name;
            _input.Drag(desktopFrom.X, desktopFrom.Y, desktopTo.X, desktopTo.Y, args.Int("duration_ms", 300), desktopButton);
            _screenshots.Clear();
            Thread.Sleep(100);
            var desktopAfterCapture = RememberCapture(null, _capture.Capture(null));
            return new ActionResult(
                true,
                "drag",
                "sendinput",
                new ActionVerification(true, "desktop-screenshot-reobserve", desktopCapture.Sha256, desktopAfterCapture.Sha256),
                new
                {
                    from = desktopFrom,
                    to = desktopTo,
                    button = desktopButton,
                    held_buttons = _input.HeldMouseButtons,
                    after_screenshot_id = desktopAfterCapture.Id,
                    visual_changed = desktopCapture.Sha256 != desktopAfterCapture.Sha256,
                    visual_diff = ActionVisualDiff(desktopCapture, desktopAfterCapture)
                });
        }
        if (IsDirectScreenAction(args))
        {
            var screenFrom = (X: args.Int("from_x"), Y: args.Int("from_y"));
            var screenTo = (X: args.Int("to_x"), Y: args.Int("to_y"));
            var screenButton = InputService.PlanMouseButton(args.String("button") ?? "left").Name;
            _input.Drag(screenFrom.X, screenFrom.Y, screenTo.X, screenTo.Y, args.Int("duration_ms", 300), screenButton);
            _screenshots.Clear();
            Thread.Sleep(100);
            var screenAfterCapture = RememberCapture(null, _capture.Capture(null));
            return new ActionResult(
                true,
                "drag",
                "sendinput",
                new ActionVerification(true, "screen-input-and-desktop-reobserve", null, screenAfterCapture.Sha256),
                new { from = screenFrom, to = screenTo, button = screenButton, held_buttons = _input.HeldMouseButtons, after_screenshot_id = screenAfterCapture.Id });
        }
        var resolved = _windows.Resolve(args);
        var beforeCapture = ValidateScreenshot(args, resolved);
        var window = _windows.Activate(resolved);
        var from = ResolvePoint(args, window, beforeCapture, "from_x", "from_y");
        var to = ResolvePoint(args, window, beforeCapture, "to_x", "to_y");
        var button = InputService.PlanMouseButton(args.String("button") ?? "left").Name;
        _input.Drag(from.X, from.Y, to.X, to.Y, args.Int("duration_ms", 300), button);
        _screenshots.Clear();
        Thread.Sleep(100);
        var after = _windows.Resolve(window.Id);
        var afterCapture = RememberCapture(after, _capture.Capture(after));
        return new ActionResult(
            true,
            "drag",
            "sendinput",
            new ActionVerification(after.IsForeground, "window-and-screenshot-reobserve", beforeCapture?.Sha256, afterCapture.Sha256),
            new
            {
                from,
                to,
                button,
                held_buttons = _input.HeldMouseButtons,
                after_screenshot_id = afterCapture.Id,
                visual_changed = beforeCapture is null ? (bool?)null : beforeCapture.Sha256 != afterCapture.Sha256,
                visual_diff = ActionVisualDiff(beforeCapture, afterCapture)
            });
    }

    private object? ActionVisualDiff(ScreenshotRecord? before, CaptureResult after)
    {
        if (before is null) return null;
        if (before.SourceBounds != after.Bounds)
        {
            return new
            {
                comparable = false,
                reason = "source-bounds-changed",
                before_source_bounds = before.SourceBounds,
                after_source_bounds = after.Bounds
            };
        }

        CaptureResult comparableAfter;
        try
        {
            comparableAfter = before.ImageRegion is null
                ? after
                : _capture.Crop(after, before.ImageRegion);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return new
            {
                comparable = false,
                reason = "source-region-no-longer-fits",
                detail = error.Message,
                before_capture_bounds = before.CaptureBounds,
                after_source_bounds = after.Bounds
            };
        }

        var diff = _visualDiff.Compare(before.Capture, comparableAfter, channelThreshold: 0, tileSize: 32, maxRegions: 20);
        return new
        {
            comparable = true,
            changed = diff.Changed,
            changed_pixels = diff.ChangedPixels,
            changed_fraction = diff.ChangedFraction,
            max_channel_delta = diff.MaxChannelDelta,
            changed_image_bounds = diff.ChangedImageBounds,
            changed_screen_bounds = diff.ChangedScreenBounds,
            regions = diff.Regions,
            region_count = diff.RegionCount,
            omitted_regions = diff.OmittedRegions
        };
    }

    private object Activate(JsonElement args)
    {
        var window = _windows.Activate(_windows.Resolve(args));
        _screenshots.Clear();
        return new { ok = true, window };
    }

    private object SetWindowState(JsonElement args)
    {
        var state = args.String("state")?.ToLowerInvariant()
            ?? throw new ArgumentException("state is required");
        var window = _windows.SetState(_windows.Resolve(args), state, args.Int("timeout_ms", 3000));
        _screenshots.Clear();
        return new
        {
            ok = true,
            state,
            backend = "win32-show-window",
            window,
            verification = new { verified = true, is_minimized = window.IsMinimized, is_maximized = window.IsMaximized }
        };
    }

    private ActionResult SetWindowBounds(JsonElement args)
    {
        var before = _windows.Resolve(args);
        var requested = new RectDto(args.Int("x"), args.Int("y"), args.Int("width"), args.Int("height"));
        var activate = args.Bool("activate");
        var after = _windows.SetBounds(before, requested, activate, args.Int("timeout_ms", 3000));
        _screenshots.Clear();
        return new ActionResult(
            true,
            "set_window_bounds",
            "win32-set-window-pos",
            new ActionVerification(true, "exact-physical-window-bounds", $"{before.Bounds.X},{before.Bounds.Y},{before.Bounds.Width},{before.Bounds.Height}", $"{after.Bounds.X},{after.Bounds.Y},{after.Bounds.Width},{after.Bounds.Height}"),
            new { requested, window = after, activate, coordinate_space = "physical-screen-pixels" });
    }

    private object EndSession()
    {
        var errors = new List<Exception>();
        var buttonsBefore = _input.HeldMouseButtons.Count;
        try { _input.ReleaseAllMouseButtons(); } catch (Exception exception) { errors.Add(exception); }
        var releasedButtons = buttonsBefore - _input.HeldMouseButtons.Count;
        var keysBefore = _input.HeldKeys.Count;
        try { _input.ReleaseAllKeys(); } catch (Exception exception) { errors.Add(exception); }
        var releasedKeys = keysBefore - _input.HeldKeys.Count;
        _uia.ClearSession();
        _windows.ClearSession();
        _screenshots.Clear();
        _observations.Clear();
        var discardedClipboardBackups = _clipboard.ClearSession();
        if (errors.Count > 0) throw new AggregateException("One or more held inputs could not be released while ending the session.", errors);
        return new { ok = true, session_id = _sessionId, ended_at = DateTimeOffset.UtcNow, released_keys = releasedKeys, released_buttons = releasedButtons, discarded_clipboard_backups = discardedClipboardBackups };
    }

    private static bool HasWindowSelector(JsonElement args) =>
        args.Long("window_id") != 0 ||
        !string.IsNullOrWhiteSpace(args.String("title")) ||
        !string.IsNullOrWhiteSpace(args.String("app"));

    private ScreenshotRecord ResolveCachedScreenshot(JsonElement args, string operation, out WindowDescriptor? window)
    {
        var screenshotId = args.String("screenshot_id")
            ?? throw new ArgumentException("screenshot_id is required");
        return ResolveCachedScreenshot(args, screenshotId, operation, out window);
    }

    private ScreenshotRecord ResolveCachedScreenshot(JsonElement args, string screenshotId, string operation, out WindowDescriptor? window)
    {
        if (args.Bool("desktop") || HasWindowSelector(args))
            throw new ArgumentException($"screenshot_id cannot be combined with desktop=true or a window selector. The cached screenshot is the authoritative source for {operation}.");
        if (!_screenshots.TryGetValue(screenshotId, out var source))
            throw new InvalidOperationException($"Unknown or expired screenshot_id. Capture or observe the intended source again before {operation}.");
        ValidateScreenshotAge(args, source);
        window = ResolveVisualSource(source);
        return source;
    }

    private static void WriteCapture(string path, CaptureResult capture) =>
        File.WriteAllBytes(path, Convert.FromBase64String(capture.Data));

    private static bool IsDirectScreenAction(JsonElement args) =>
        string.Equals(args.String("coordinate_space"), "screen", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(args.String("screenshot_id")) &&
        !HasWindowSelector(args);

    private CaptureResult RememberCapture(
        WindowDescriptor? window,
        CaptureResult capture,
        RectDto? sourceBounds = null,
        RectDto? imageRegion = null)
    {
        _screenshots[capture.Id] = new ScreenshotRecord(
            window?.Id,
            window?.Bounds,
            sourceBounds ?? capture.Bounds,
            imageRegion,
            capture);
        while (_screenshots.Count > 1 &&
               (_screenshots.Count > MaxScreenshotCacheEntries ||
                _screenshots.Values.Sum(record => (long)record.Capture.Data.Length) > MaxScreenshotCacheBase64Chars))
            _screenshots.Remove(_screenshots.First().Key);
        return capture;
    }

    private ScreenshotRecord? ValidateScreenshot(JsonElement args, WindowDescriptor window)
    {
        var screenshotId = args.String("screenshot_id");
        if (string.IsNullOrWhiteSpace(screenshotId)) return null;
        if (!_screenshots.TryGetValue(screenshotId, out var screenshot))
            throw new InvalidOperationException("Unknown or expired screenshot_id. Capture or snapshot the target window again before using pixel coordinates.");
        if (screenshot.WindowId is null)
            throw new InvalidOperationException("The screenshot_id belongs to the virtual desktop. Use it without a window selector so input targets the visible desktop without foreground activation.");
        if (screenshot.WindowId.Value != window.Id)
            throw new InvalidOperationException("The screenshot_id belongs to a different window. Capture or snapshot the selected window again.");
        if (screenshot.WindowBounds != window.Bounds)
            throw new InvalidOperationException("The target window moved or resized after the screenshot. Capture or snapshot it again before using pixel coordinates.");
        ValidateScreenshotAge(args, screenshot);
        return screenshot;
    }

    private ScreenshotRecord? ValidateDesktopScreenshot(JsonElement args)
    {
        var screenshotId = args.String("screenshot_id");
        if (string.IsNullOrWhiteSpace(screenshotId)) return null;
        if (!_screenshots.TryGetValue(screenshotId, out var screenshot))
            throw new InvalidOperationException("Unknown or expired screenshot_id. Capture the virtual desktop again before using screenshot coordinates.");
        if (screenshot.WindowId is not null) return null;
        if (HasWindowSelector(args))
            throw new InvalidOperationException("A virtual-desktop screenshot_id cannot be combined with a window selector. Use the desktop observation directly without foreground activation.");
        if (!string.Equals(args.String("coordinate_space"), "screenshot", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A virtual-desktop screenshot_id must be used with coordinate_space=screenshot.");
        var currentBounds = VirtualDesktopBounds();
        if (screenshot.SourceBounds != currentBounds)
            throw new InvalidOperationException("The virtual desktop topology changed after the screenshot. Capture it again before using screenshot coordinates.");
        ValidateScreenshotAge(args, screenshot);
        return screenshot;
    }

    private static void ValidateScreenshotAge(JsonElement args, ScreenshotRecord screenshot)
    {
        var maxAge = Math.Clamp(args.Int("max_age_ms", 15_000), 100, 120_000);
        if (DateTimeOffset.UtcNow - screenshot.CapturedAt > TimeSpan.FromMilliseconds(maxAge))
            throw new InvalidOperationException("The screenshot_id is stale. Capture or snapshot again before using pixel coordinates.");
    }

    private static RectDto VirtualDesktopBounds() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));

    private WindowInspection RememberObservation(WindowInspection inspection)
    {
        _observations[inspection.ObservationId] = inspection;
        while (_observations.Count > 16) _observations.Remove(_observations.First().Key);
        return inspection;
    }

    private (int X, int Y) ResolvePoint(
        JsonElement args,
        WindowDescriptor window,
        ScreenshotRecord? screenshot,
        string xName,
        string yName,
        int defaultX = 0,
        int defaultY = 0)
    {
        var x = args.Int(xName, defaultX);
        var y = args.Int(yName, defaultY);
        var coordinateSpace = args.String("coordinate_space")?.ToLowerInvariant()
            ?? (args.Bool("relative", true) ? "window" : "screen");
        return coordinateSpace switch
        {
            "window" => _input.WindowPoint(window, x, y, true),
            "screen" => (x, y),
            "screenshot" => ScreenshotPoint(screenshot, x, y),
            _ => throw new ArgumentException("coordinate_space must be window, screen, or screenshot")
        };
    }

    private static (int X, int Y) ScreenshotPoint(ScreenshotRecord? screenshot, int x, int y)
    {
        if (screenshot is null)
            throw new InvalidOperationException("coordinate_space=screenshot requires a valid screenshot_id from capture or snapshot.");
        if (x < 0 || y < 0 || x >= screenshot.CaptureBounds.Width || y >= screenshot.CaptureBounds.Height)
            throw new ArgumentOutOfRangeException(nameof(x), "Screenshot coordinates must fall inside the captured image.");
        return (screenshot.CaptureBounds.X + x, screenshot.CaptureBounds.Y + y);
    }

    private static JsonObject ToJsonObject(object value)
    {
        var json = JsonSerializer.Serialize(value, ProtocolJson.Options);
        return JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("Expected a JSON object from the OCR backend.");
    }

    private static void AddCaptureMetadata(JsonObject node, CaptureResult capture)
    {
        node["screenshot_id"] = capture.Id;
        node["captured_at"] = JsonValue.Create(capture.CapturedAt);
        node["capture_bounds"] = JsonSerializer.SerializeToNode(capture.Bounds, ProtocolJson.Options);
        node["coordinate_space"] = "screenshot";
        node["sha256"] = capture.Sha256;
    }

    private static void AddOcrMatch(
        JsonArray target,
        JsonObject source,
        string kind,
        string query,
        string mode,
        StringComparison comparison,
        CaptureResult capture,
        int limit)
    {
        if (target.Count >= limit) return;
        var text = source["text"]?.GetValue<string>() ?? string.Empty;
        var matched = mode == "exact"
            ? string.Equals(text, query, comparison)
            : text.Contains(query, comparison);
        if (!matched || source["bounds"] is not JsonObject bounds) return;

        var x = (int)Math.Round(bounds["x"]?.GetValue<double>() ?? 0d);
        var y = (int)Math.Round(bounds["y"]?.GetValue<double>() ?? 0d);
        var width = Math.Max(0, (int)Math.Round(bounds["width"]?.GetValue<double>() ?? 0d));
        var height = Math.Max(0, (int)Math.Round(bounds["height"]?.GetValue<double>() ?? 0d));
        if (width == 0 || height == 0) return;
        var screenshotBounds = new RectDto(x, y, width, height);
        var screenBounds = new RectDto(capture.Bounds.X + x, capture.Bounds.Y + y, width, height);
        target.Add(new JsonObject
        {
            ["kind"] = kind,
            ["text"] = text,
            ["bounds"] = JsonSerializer.SerializeToNode(screenshotBounds, ProtocolJson.Options),
            ["screen_bounds"] = JsonSerializer.SerializeToNode(screenBounds, ProtocolJson.Options),
            ["center"] = new JsonObject { ["x"] = x + width / 2, ["y"] = y + height / 2 }
        });
    }

    private static bool Equivalent(ControlDescriptor before, ControlDescriptor after) =>
        before.Id == after.Id &&
        before.ParentId == after.ParentId &&
        before.Depth == after.Depth &&
        before.ChildCount == after.ChildCount &&
        before.Name == after.Name &&
        before.AutomationId == after.AutomationId &&
        before.ControlType == after.ControlType &&
        before.ClassName == after.ClassName &&
        before.Bounds == after.Bounds &&
        before.IsEnabled == after.IsEnabled &&
        before.IsOffscreen == after.IsOffscreen &&
        before.HasKeyboardFocus == after.HasKeyboardFocus &&
        before.Value == after.Value &&
        before.IsReadOnly == after.IsReadOnly &&
        before.IsSelected == after.IsSelected &&
        before.ToggleState == after.ToggleState &&
        before.ExpandCollapseState == after.ExpandCollapseState &&
        before.HorizontalScrollPercent == after.HorizontalScrollPercent &&
        before.VerticalScrollPercent == after.VerticalScrollPercent &&
        before.Patterns.SequenceEqual(after.Patterns, StringComparer.Ordinal);

    private sealed record ScreenshotRecord(
        long? WindowId,
        RectDto? WindowBounds,
        RectDto SourceBounds,
        RectDto? ImageRegion,
        CaptureResult Capture)
    {
        public RectDto CaptureBounds => Capture.Bounds;
        public DateTimeOffset CapturedAt => Capture.CapturedAt;
        public string Sha256 => Capture.Sha256;
    }
}
