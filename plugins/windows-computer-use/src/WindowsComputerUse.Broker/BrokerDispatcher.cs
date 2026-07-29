using System.Diagnostics;
using System.Text.Json;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Broker;

public sealed class BrokerDispatcher : IDisposable
{
    private readonly WindowService _windows = new();
    private readonly InputService _input = new();
    private readonly CaptureService _capture = new();
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
                    windows = _windows.ListWindows(),
                    coordinate_space = "physical-screen-pixels",
                    access_mode = "full-control"
                },
                "launch_app" => Launch(args),
                "inspect_window" => Inspect(args),
                "observe_changes" => ObserveChanges(args),
                "find_controls" => Find(args),
                "invoke" => Invoke(args),
                "enter_text" => EnterText(args),
                "wait_for_ui" => Wait(args),
                "capture" => Capture(args),
                "snapshot" => Snapshot(args),
                "ocr" => await OcrAsync(args, cancellationToken),
                "click" => Click(args),
                "press_key" => PressKey(args),
                "type_text" => TypeText(args),
                "scroll" => Scroll(args),
                "drag" => Drag(args),
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
        "inspect_window" or "observe_changes" or "find_controls" or "invoke" or "enter_text" or "capture" or "snapshot" or "ocr" or
        "click" or "press_key" or "type_text" or "scroll" or "drag" or "activate_window";

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
        if (temporary)
        {
            path = Path.Combine(Path.GetTempPath(), $"wcu-ocr-{Guid.NewGuid():N}.png");
            var window = args.Bool("desktop") ? null : _windows.Resolve(args);
            _capture.Capture(window, path);
        }
        try { return await _ocr.RecognizeAsync(path!, args.String("language"), cancellationToken); }
        finally { if (temporary && File.Exists(path)) File.Delete(path); }
    }

    private ActionResult Click(JsonElement args)
    {
        var resolved = _windows.Resolve(args);
        var beforeCapture = ValidateScreenshot(args, resolved);
        var window = _windows.Activate(resolved);
        var point = _input.WindowPoint(window, args.Int("x"), args.Int("y"), args.Bool("relative", true));
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
        var point = _input.WindowPoint(window, args.Int("x", window.Bounds.Width / 2), args.Int("y", window.Bounds.Height / 2), args.Bool("relative", true));
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
        var relative = args.Bool("relative", true);
        var from = _input.WindowPoint(window, args.Int("from_x"), args.Int("from_y"), relative);
        var to = _input.WindowPoint(window, args.Int("to_x"), args.Int("to_y"), relative);
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
            _screenshots[capture.Id] = new ScreenshotRecord(window.Id, window.Bounds, capture.CapturedAt, capture.Sha256);
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

    private sealed record ScreenshotRecord(long WindowId, RectDto Bounds, DateTimeOffset CapturedAt, string Sha256);
}
