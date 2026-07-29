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
                    backends = new[] { "uia3", "win32", "sendinput", "print-window", "windows-media-ocr" },
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
                "find_controls" => Find(args),
                "invoke" => Invoke(args),
                "enter_text" => EnterText(args),
                "wait_for_ui" => Wait(args),
                "capture" => Capture(args),
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
        "inspect_window" or "find_controls" or "invoke" or "enter_text" or "capture" or "ocr" or
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
        return _uia.Inspect(window, args.Int("limit", 400));
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
        return _uia.Invoke(window, args.String("control_id"), args);
    }

    private ActionResult EnterText(JsonElement args)
    {
        var text = args.String("text") ?? throw new ArgumentException("text is required");
        var window = _windows.Activate(_windows.Resolve(args));
        return _uia.EnterText(window, args.String("control_id"), args, text, args.Bool("append"));
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
        return _capture.Capture(window, args.String("path"));
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
        var window = _windows.Activate(_windows.Resolve(args));
        var point = _input.WindowPoint(window, args.Int("x"), args.Int("y"), args.Bool("relative", true));
        var before = $"foreground={window.Id};point={point.X},{point.Y}";
        _input.Click(point.X, point.Y, args.String("button") ?? "left", args.Int("count", 1));
        Thread.Sleep(100);
        var after = _windows.Resolve(window.Id);
        return new ActionResult(true, "click", "sendinput", new ActionVerification(true, "window-reobserve", before, $"foreground={after.IsForeground}"), new { x = point.X, y = point.Y });
    }

    private ActionResult PressKey(JsonElement args)
    {
        var key = args.String("key") ?? throw new ArgumentException("key is required");
        var window = _windows.Activate(_windows.Resolve(args));
        _input.PressChord(key);
        Thread.Sleep(60);
        return new ActionResult(true, "press_key", "sendinput", new ActionVerification(true, "foreground-window", window.Id.ToString(), _windows.Resolve(window.Id).IsForeground.ToString()));
    }

    private ActionResult TypeText(JsonElement args)
    {
        var text = args.String("text") ?? throw new ArgumentException("text is required");
        var window = _windows.Activate(_windows.Resolve(args));
        _input.TypeText(text);
        Thread.Sleep(80);
        return new ActionResult(true, "type_text", "sendinput-unicode", new ActionVerification(true, "foreground-window", window.Id.ToString(), _windows.Resolve(window.Id).IsForeground.ToString()));
    }

    private ActionResult Scroll(JsonElement args)
    {
        var window = _windows.Activate(_windows.Resolve(args));
        var point = _input.WindowPoint(window, args.Int("x", window.Bounds.Width / 2), args.Int("y", window.Bounds.Height / 2), args.Bool("relative", true));
        _input.Scroll(point.X, point.Y, args.Int("vertical"), args.Int("horizontal"));
        return new ActionResult(true, "scroll", "sendinput", new ActionVerification(true, "window-reobserve", window.Id.ToString(), _windows.Resolve(window.Id).Id.ToString()), new { x = point.X, y = point.Y });
    }

    private ActionResult Drag(JsonElement args)
    {
        var window = _windows.Activate(_windows.Resolve(args));
        var relative = args.Bool("relative", true);
        var from = _input.WindowPoint(window, args.Int("from_x"), args.Int("from_y"), relative);
        var to = _input.WindowPoint(window, args.Int("to_x"), args.Int("to_y"), relative);
        _input.Drag(from.X, from.Y, to.X, to.Y, args.Int("duration_ms", 300));
        return new ActionResult(true, "drag", "sendinput", new ActionVerification(true, "window-reobserve", window.Id.ToString(), _windows.Resolve(window.Id).Id.ToString()), new { from, to });
    }

    private object Activate(JsonElement args)
    {
        var window = _windows.Activate(_windows.Resolve(args));
        return new { ok = true, window };
    }

    private object EndSession()
    {
        _uia.ClearSession();
        return new { ok = true, session_id = _sessionId, ended_at = DateTimeOffset.UtcNow };
    }
}
