using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Broker;

public sealed class BrokerDispatcher : IDisposable
{
    private readonly WindowService _windows = new();
    private readonly InputService _input = new();
    private readonly CaptureService _capture = new();
    private readonly DisplayService _displays = new();
    private readonly OcrService _ocr = new();
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
                    backends = new[] { "uia3", "win32", "sendinput", "windows-graphics-capture", "print-window", "windows-media-ocr" },
                    dpi_awareness = "per-monitor-v2"
                },
                "list_windows" => new
                {
                    windows = _windows.ListWindows(args.Bool("include_untitled")),
                    coordinate_space = "physical-screen-pixels",
                    access_mode = "full-control"
                },
                "display_info" => _displays.GetTopology(),
                "launch_app" => Launch(args),
                "wait_for_window" => await WaitForWindowAsync(args, cancellationToken),
                "inspect_window" => Inspect(args),
                "observe_changes" => ObserveChanges(args),
                "find_controls" => Find(args),
                "invoke" => Invoke(args),
                "enter_text" => EnterText(args),
                "wait_for_ui" => Wait(args),
                "capture" => Capture(args),
                "snapshot" => Snapshot(args),
                "ocr" => await OcrAsync(args, cancellationToken),
                "find_text" => await FindTextAsync(args, cancellationToken),
                "click" => Click(args),
                "press_key" => PressKey(args),
                "type_text" => TypeText(args),
                "scroll" => Scroll(args),
                "drag" => Drag(args),
                "set_window_state" => SetWindowState(args),
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

    public void Dispose() => _uia.Dispose();

    private static bool NeedsUiLock(string method) => method is
        "inspect_window" or "observe_changes" or "find_controls" or "invoke" or "enter_text" or "capture" or "snapshot" or "ocr" or "find_text" or
        "click" or "press_key" or "type_text" or "scroll" or "drag" or "set_window_state" or "activate_window";

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
        return new WindowDiff(window, previousId, current.ObservationId, current.CapturedAt, changes, current.FocusedControlId);
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

    private object Wait(JsonElement args)
    {
        var window = _windows.Resolve(args);
        return _uia.WaitFor(window, args, args.String("state") ?? "exists", args.Int("timeout_ms", 10_000), args.Int("poll_ms", 100));
    }

    private CaptureResult Capture(JsonElement args)
    {
        var desktop = args.Bool("desktop");
        var window = desktop ? null : _windows.Resolve(args);
        return RememberCapture(window, _capture.Capture(window, args.String("path")));
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
        var suppliedPath = args.String("path");
        var temporary = string.IsNullOrWhiteSpace(suppliedPath);
        var path = suppliedPath;
        CaptureResult? capture = null;
        WindowDescriptor? capturedWindow = null;
        if (temporary)
        {
            path = Path.Combine(Path.GetTempPath(), $"wcu-ocr-{Guid.NewGuid():N}.png");
            capturedWindow = args.Bool("desktop") ? null : _windows.Resolve(args);
            capture = RememberCapture(capturedWindow, _capture.Capture(capturedWindow, path));
        }
        try
        {
            var node = ToJsonObject(await _ocr.RecognizeAsync(path!, args.String("language"), cancellationToken));
            // Only window captures are cached and therefore safe to feed back into
            // screenshot-bound input. Desktop OCR remains recognition-only.
            if (capture is not null && capturedWindow is not null) AddCaptureMetadata(node, capture);
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
        var window = _windows.Resolve(args);
        var path = Path.Combine(Path.GetTempPath(), $"wcu-find-text-{Guid.NewGuid():N}.png");
        var capture = RememberCapture(window, _capture.Capture(window, path));
        try
        {
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

    private ActionResult Click(JsonElement args)
    {
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
                visual_changed = beforeCapture is null ? (bool?)null : beforeCapture.Sha256 != afterCapture.Sha256
            });
    }

    private ActionResult PressKey(JsonElement args)
    {
        var key = args.String("key") ?? throw new ArgumentException("key is required");
        var window = _windows.Activate(_windows.Resolve(args));
        _input.PressChord(key);
        _screenshots.Clear();
        Thread.Sleep(60);
        return new ActionResult(true, "press_key", "sendinput", new ActionVerification(true, "foreground-window", window.Id.ToString(), _windows.Resolve(window.Id).IsForeground.ToString()));
    }

    private ActionResult TypeText(JsonElement args)
    {
        var text = args.String("text") ?? throw new ArgumentException("text is required");
        var window = _windows.Activate(_windows.Resolve(args));
        _input.TypeText(text);
        _screenshots.Clear();
        Thread.Sleep(80);
        return new ActionResult(true, "type_text", "sendinput-unicode", new ActionVerification(true, "foreground-window", window.Id.ToString(), _windows.Resolve(window.Id).IsForeground.ToString()));
    }

    private ActionResult Scroll(JsonElement args)
    {
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
            new { x = point.X, y = point.Y, after_screenshot_id = afterCapture.Id, visual_changed = beforeCapture is null ? (bool?)null : beforeCapture.Sha256 != afterCapture.Sha256 });
    }

    private ActionResult Drag(JsonElement args)
    {
        var resolved = _windows.Resolve(args);
        var beforeCapture = ValidateScreenshot(args, resolved);
        var window = _windows.Activate(resolved);
        var from = ResolvePoint(args, window, beforeCapture, "from_x", "from_y");
        var to = ResolvePoint(args, window, beforeCapture, "to_x", "to_y");
        _input.Drag(from.X, from.Y, to.X, to.Y, args.Int("duration_ms", 300));
        _screenshots.Clear();
        Thread.Sleep(100);
        var after = _windows.Resolve(window.Id);
        var afterCapture = RememberCapture(after, _capture.Capture(after));
        return new ActionResult(
            true,
            "drag",
            "sendinput",
            new ActionVerification(after.IsForeground, "window-and-screenshot-reobserve", beforeCapture?.Sha256, afterCapture.Sha256),
            new { from, to, after_screenshot_id = afterCapture.Id, visual_changed = beforeCapture is null ? (bool?)null : beforeCapture.Sha256 != afterCapture.Sha256 });
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

    private object EndSession()
    {
        _uia.ClearSession();
        _screenshots.Clear();
        _observations.Clear();
        return new { ok = true, session_id = _sessionId, ended_at = DateTimeOffset.UtcNow };
    }

    private CaptureResult RememberCapture(WindowDescriptor? window, CaptureResult capture)
    {
        if (window is not null)
        {
            _screenshots[capture.Id] = new ScreenshotRecord(window.Id, window.Bounds, capture.Bounds, capture.CapturedAt, capture.Sha256);
            while (_screenshots.Count > 32) _screenshots.Remove(_screenshots.First().Key);
        }
        return capture;
    }

    private ScreenshotRecord? ValidateScreenshot(JsonElement args, WindowDescriptor window)
    {
        var screenshotId = args.String("screenshot_id");
        if (string.IsNullOrWhiteSpace(screenshotId)) return null;
        if (!_screenshots.TryGetValue(screenshotId, out var screenshot))
            throw new InvalidOperationException("Unknown or expired screenshot_id. Capture or snapshot the target window again before using pixel coordinates.");
        if (screenshot.WindowId != window.Id)
            throw new InvalidOperationException("The screenshot_id belongs to a different window. Capture or snapshot the selected window again.");
        if (screenshot.Bounds != window.Bounds)
            throw new InvalidOperationException("The target window moved or resized after the screenshot. Capture or snapshot it again before using pixel coordinates.");
        var maxAge = Math.Clamp(args.Int("max_age_ms", 15_000), 100, 120_000);
        if (DateTimeOffset.UtcNow - screenshot.CapturedAt > TimeSpan.FromMilliseconds(maxAge))
            throw new InvalidOperationException("The screenshot_id is stale. Capture or snapshot the target window again before using pixel coordinates.");
        return screenshot;
    }

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
        before.Patterns.SequenceEqual(after.Patterns, StringComparer.Ordinal);

    private sealed record ScreenshotRecord(long WindowId, RectDto Bounds, RectDto CaptureBounds, DateTimeOffset CapturedAt, string Sha256);
}
