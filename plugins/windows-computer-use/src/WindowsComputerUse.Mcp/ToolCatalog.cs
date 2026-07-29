using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Mcp;

public static class ToolCatalog
{
    public static IReadOnlyList<ToolDefinition> All { get; } =
    [
        Tool("list_windows", "List visible top-level Windows windows with native class and owner/root-owner relationships. Always use a returned window_id instead of guessing a target.",
            Props(("include_untitled", S("boolean", "Include visible titleless top-level windows, default false.")))),
        Tool("display_info", "Return physical virtual-desktop bounds plus every monitor's bounds, work area, primary flag, effective DPI, and scale percentage.", Props()),
        Tool("pointer_position", "Return the current mouse pointer position in physical virtual-desktop screen pixels.", Props()),
        Tool("window_from_point", "Map one physical virtual-desktop pixel to the current top-level root window plus the actual native child HWND/class/title under that point without activating or clicking.",
            Props(("x", S("integer", "Physical virtual-desktop X coordinate.")), ("y", S("integer", "Physical virtual-desktop Y coordinate."))), ["x", "y"]),
        Tool("launch_app", "Launch any app, executable, file, URI, or registered shell target in full-control mode.",
            Props(("app", S("string", "Executable path, app id, file, or URI.")), ("arguments", S("string", "Optional command-line arguments.")), ("wait_ms", S("integer", "Wait for initial UI readiness."))), ["app"]),
        Tool("wait_for_window", "Wait for a top-level window or owned transient dialog to appear or disappear without blind sleeps.",
            Props(("title", S("string", "Window-title substring.")), ("app", S("string", "App/process substring.")), ("window_class", S("string", "Native window-class substring.")), ("process_id", S("integer", "Exact process id.")), ("owner_window_id", S("integer", "Exact immediate owner window id.")), ("root_owner_window_id", S("integer", "Exact root-owner window id.")), ("state", Enum("exists", "absent")), ("include_untitled", S("boolean", "Include titleless top-level windows.")), ("timeout_ms", S("integer", "Timeout up to 120000 ms.")), ("poll_ms", S("integer", "Polling interval.")))),
        Tool("inspect_window", "Inspect one window through UI Automation 3 and return a semantic control tree with stable control ids.",
            WindowProps(("limit", S("integer", "Maximum UIA elements, default 400.")))),
        Tool("observe_changes", "Compare a fresh hierarchical UIA observation with a cached observation id and return only added, removed, or changed controls.",
            WindowProps(("previous_observation_id", S("string", "Observation id from inspect_window or snapshot.")), ("limit", S("integer", "Maximum UIA elements, default 400."))), ["previous_observation_id"]),
        Tool("find_controls", "Find semantic controls by accessible name, AutomationId, control type, class, or enabled state.",
            QueryProps(("limit", S("integer", "Maximum returned controls, default 50.")))),
        Tool("invoke", "Invoke a semantic control. Stale ids are automatically re-resolved; unsupported patterns fall back to a center click.",
            QueryProps(("control_id", S("string", "Stable id from inspect_window or find_controls.")))),
        Tool("perform_secondary_action", "Perform an explicit UIA secondary action such as focus, select, toggle, expand/collapse, or semantic scroll on one control.",
            QueryProps(("control_id", S("string", "Stable control id.")), ("action", Enum("focus", "raise", "select", "add_to_selection", "remove_from_selection", "toggle", "expand", "collapse", "scroll_up", "scroll_down", "scroll_left", "scroll_right"))), ["action"]),
        Tool("enter_text", "Set or type Unicode text into a semantic control, preferring UIA ValuePattern and falling back to SendInput.",
            QueryProps(("control_id", S("string", "Stable control id.")), ("text", S("string", "Text to enter.")), ("append", S("boolean", "Append instead of replacing."))), ["text"]),
        Tool("paste_text", "Atomically preserve the Windows clipboard, focus one semantic control, paste through real Ctrl+V, verify observable Value state, and restore every prior clipboard format; tracked held keys must be released first.",
            QueryProps(("control_id", S("string", "Stable control id.")), ("text", S("string", "Unicode text to paste.")), ("append", S("boolean", "Move to the end and append instead of replacing all text.")), ("timeout_ms", S("integer", "Value verification timeout from 100 to 10000 ms, default 2000.")), ("settle_ms", S("integer", "Fallback settling time for controls without UIA Value state, default 200 ms."))), ["text"]),
        Tool("copy_text", "Atomically focus one semantic control, optionally select all, copy through real Ctrl+C, retry at most once after semantic refocus when publication is missing or disagrees with observable UIA selection, return Unicode text, and restore every prior clipboard format; tracked held keys must be released first.",
            QueryProps(("control_id", S("string", "Stable control id.")), ("selection", Enum("current", "all")), ("timeout_ms", S("integer", "Clipboard sequence-change timeout from 100 to 10000 ms, default 2000.")))),
        Tool("wait_for_ui", "Poll UIA until a control matches an existence, visibility, focus, Value, selection, toggle, expand/collapse, or read-only state without blind sleeps.",
            QueryProps(("control_id", S("string", "Stable control id.")), ("state", Enum("exists", "absent", "visible", "hidden", "enabled", "focused", "value_equals", "value_contains", "selected", "unselected", "toggle_on", "toggle_off", "toggle_indeterminate", "expanded", "collapsed", "readonly", "editable")), ("expected_value", S("string", "Required for value_equals/value_contains.")), ("case_sensitive", S("boolean", "Use ordinal case-sensitive Value comparison.")), ("timeout_ms", S("integer", "Timeout up to 120000 ms.")), ("poll_ms", S("integer", "Polling interval.")))),
        Tool("wait_for_visual_change", "Poll the exact PNG content of the same window or virtual desktop until it differs from a fresh cached screenshot, then return a new actionable PNG instead of relying on a blind sleep.",
            Props(("screenshot_id", S("string", "Fresh screenshot id from capture, snapshot, or observe_desktop; its original source is reused automatically.")), ("timeout_ms", S("integer", "Timeout from 100 to 120000 ms, default 10000.")), ("poll_ms", S("integer", "Capture polling interval from 25 to 2000 ms, default 100.")), ("max_age_ms", S("integer", "Maximum age of the source screenshot, default 15000 ms."))), ["screenshot_id"]),
        Tool("wait_for_visual_stable", "Poll the exact PNG content of the same window or virtual desktop until fresh samples remain unchanged for a continuous stability interval, then return a new actionable PNG.",
            Props(("screenshot_id", S("string", "Fresh screenshot id whose original window or virtual-desktop source is reused automatically.")), ("stable_ms", S("integer", "Required continuous exact-image stability from 100 to 10000 ms, default 500.")), ("timeout_ms", S("integer", "Overall timeout from 100 to 120000 ms, default 10000.")), ("poll_ms", S("integer", "Capture polling interval from 25 to 2000 ms, default 100.")), ("max_age_ms", S("integer", "Maximum age of the source screenshot, default 15000 ms."))), ["screenshot_id"]),
        Tool("compare_screenshots", "Compare exact cached pixels from two fresh screenshots of the same source and region, returning changed-pixel metrics plus localized tile-connected bounds.",
            Props(("before_screenshot_id", S("string", "Fresh baseline screenshot id.")), ("after_screenshot_id", S("string", "Fresh comparison screenshot id from the same source and region.")), ("channel_threshold", S("integer", "Per-channel delta from 0 to 255 that must be exceeded; default 0 exact.")), ("tile_size", S("integer", "Localization tile size from 8 to 128 pixels, default 32.")), ("max_regions", S("integer", "Maximum localized regions from 1 to 200, default 50.")), ("max_age_ms", S("integer", "Maximum age of both cached screenshots, default 15000 ms."))), ["before_screenshot_id", "after_screenshot_id"]),
        Tool("capture", "Capture a window through Windows Graphics Capture with fallback, or capture the virtual desktop, and return PNG content plus an actionable screenshot id.",
            WindowProps(("desktop", S("boolean", "Capture the full virtual desktop instead of one window.")), ("path", S("string", "Optional absolute output path.")))),
        Tool("capture_region", "Crop an image-relative rectangle from exact cached screenshot pixels or a fresh window/virtual-desktop frame and return an actionable PNG.",
            WindowProps(("desktop", S("boolean", "Capture a fresh full virtual-desktop source instead of one window; cannot be combined with screenshot_id.")), ("screenshot_id", S("string", "Optional fresh cached screenshot to crop exactly; authoritative and cannot be combined with desktop or window selectors.")), ("max_age_ms", S("integer", "Maximum cached screenshot age, default 15000 ms.")), ("x", S("integer", "Region left in source-image pixels.")), ("y", S("integer", "Region top in source-image pixels.")), ("width", S("integer", "Positive region width fully inside the source image.")), ("height", S("integer", "Positive region height fully inside the source image.")), ("path", S("string", "Optional absolute cropped-PNG output path."))), ["x", "y", "width", "height"]),
        Tool("observe_desktop", "Return one actionable virtual-desktop PNG together with display topology, visible top-level windows, and the current pointer position without activating a window.",
            Props(("include_untitled", S("boolean", "Include visible titleless top-level windows.")), ("path", S("string", "Optional absolute output path.")))),
        Tool("snapshot", "Return one atomic computer-use observation containing the UIA semantic state and a fresh window image with screenshot id, timestamp, and content hash.",
            WindowProps(("limit", S("integer", "Maximum UIA elements, default 400.")), ("path", S("string", "Optional absolute output path.")))),
        Tool("ocr", "Run Windows.Media.Ocr on exact cached screenshot pixels, a fresh window/desktop capture, or an existing PNG; cached and fresh captures remain actionable.",
            WindowProps(("desktop", S("boolean", "OCR a fresh virtual-desktop capture; cannot be combined with screenshot_id or path.")), ("screenshot_id", S("string", "Optional fresh cached screenshot to recognize exactly; authoritative and cannot be combined with path, desktop, or window selectors.")), ("max_age_ms", S("integer", "Maximum cached screenshot age, default 15000 ms.")), ("path", S("string", "Existing image path; authoritative and mutually exclusive with screenshot_id, desktop, and window selectors.")), ("language", S("string", "Optional BCP-47 language tag.")))),
        Tool("find_text", "Run Windows OCR over exact cached screenshot pixels or a fresh window/desktop capture and return actionable matching line/word bounds.",
            WindowProps(("desktop", S("boolean", "Search a fresh full virtual-desktop capture; cannot be combined with screenshot_id.")), ("screenshot_id", S("string", "Optional fresh cached screenshot to search exactly; authoritative and cannot be combined with desktop or window selectors.")), ("max_age_ms", S("integer", "Maximum cached screenshot age, default 15000 ms.")), ("text", S("string", "OCR text to locate.")), ("match", Enum("exact", "contains")), ("case_sensitive", S("boolean", "Use ordinal case-sensitive matching.")), ("language", S("string", "Optional BCP-47 language tag.")), ("limit", S("integer", "Maximum matches, default 50."))), ["text"]),
        Tool("find_image", "Locate a local PNG/JPEG template in exact cached screenshot pixels or a fresh window/desktop capture across a bounded optional scale range, returning actionable coordinates.",
            WindowProps(("desktop", S("boolean", "Search a fresh full virtual-desktop capture; cannot be combined with screenshot_id.")), ("screenshot_id", S("string", "Optional fresh cached screenshot to search exactly; authoritative and cannot be combined with desktop or window selectors.")), ("max_age_ms", S("integer", "Maximum cached screenshot age, default 15000 ms.")), ("template_path", S("string", "Local image template path.")), ("threshold", S("number", "Color-similarity threshold from 0.5 to 1.0, default 0.92.")), ("max_results", S("integer", "Maximum non-overlapping matches, default 10.")), ("scale_min", S("number", "Minimum template scale from 0.25 to 4.0, default 1.0.")), ("scale_max", S("number", "Maximum template scale from 0.25 to 4.0, default 1.0.")), ("scale_step", S("number", "Scale increment from 0.01 to 1.0; at most 25 scales, default 0.1."))), ["template_path"]),
        Tool("read_clipboard_text", "Read Unicode text and direct data-format metadata from the current Windows clipboard.", Props()),
        Tool("write_clipboard_text", "Replace the Windows clipboard with Unicode text, verify the write, and optionally retain a session-local full-format backup token for verified restoration.",
            Props(("text", S("string", "Unicode text to place on the clipboard.")), ("preserve_previous", S("boolean", "Safely materialize all direct clipboard formats and return a restore token before replacing them; default true."))), ["text"]),
        Tool("restore_clipboard", "Restore every safely materialized clipboard format captured by write_clipboard_text and consume its session-local backup token.",
            Props(("backup_id", S("string", "Backup token returned by write_clipboard_text."))), ["backup_id"]),
        Tool("move_pointer", "Move or smoothly hover the mouse pointer without clicking or activating a target window. Screen coordinates need no window selector.",
            WindowProps(("x", S("integer", "Target X coordinate.")), ("y", S("integer", "Target Y coordinate.")), ("coordinate_space", Enum("window", "screen", "screenshot")), ("screenshot_id", S("string", "Fresh screenshot id; required for screenshot coordinates and can identify its window.")), ("max_age_ms", S("integer", "Maximum screenshot age, default 15000 ms.")), ("duration_ms", S("integer", "Smooth movement duration from 0 to 10000 ms."))), ["x", "y"]),
        Tool("click", "Click physical pixels in a selected window, directly in screen space, or through a fresh virtual-desktop screenshot id. Coordinates are window-relative when a window is selected.",
            WindowProps(("x", S("integer", "X coordinate.")), ("y", S("integer", "Y coordinate.")), ("coordinate_space", Enum("window", "screen", "screenshot")), ("relative", S("boolean", "Legacy window-relative flag, default true; coordinate_space takes precedence.")), ("button", Enum("left", "right", "middle", "x1", "x2")), ("count", S("integer", "Click count 1-4.")), ("screenshot_id", S("string", "Bind the coordinates to a recent capture/snapshot; required for screenshot coordinates.")), ("max_age_ms", S("integer", "Maximum screenshot age, default 15000 ms."))), ["x", "y"]),
        Tool("mouse_down", "Move to a window, direct-screen, or desktop-screenshot pixel and hold one mouse button across subsequent actions. The hold is released by mouse_up, end_session, or broker disposal.",
            WindowProps(("x", S("integer", "X coordinate.")), ("y", S("integer", "Y coordinate.")), ("coordinate_space", Enum("window", "screen", "screenshot")), ("button", Enum("left", "right", "middle", "x1", "x2")), ("screenshot_id", S("string", "Bind coordinates to a recent capture/snapshot.")), ("max_age_ms", S("integer", "Maximum screenshot age, default 15000 ms."))), ["x", "y"]),
        Tool("mouse_up", "Move to a window, direct-screen, or desktop-screenshot pixel and release one mouse button previously held with mouse_down.",
            WindowProps(("x", S("integer", "X coordinate.")), ("y", S("integer", "Y coordinate.")), ("coordinate_space", Enum("window", "screen", "screenshot")), ("button", Enum("left", "right", "middle", "x1", "x2")), ("screenshot_id", S("string", "Bind coordinates to a recent capture/snapshot.")), ("max_age_ms", S("integer", "Maximum screenshot age, default 15000 ms."))), ["x", "y"]),
        Tool("press_key", "Press and release a + separated key chord, with implied Shift/Ctrl/Alt and optional repeat timing. Set desktop=true to preserve the current foreground instead of selecting and activating a window.",
            WindowProps(("desktop", S("boolean", "Send to the current foreground focus without a window selector or activation.")), ("key", S("string", "Key or chord, such as ctrl+s, shift+tab, plus, f24, or volumeup.")), ("repeat", S("integer", "Repeat count from 1 to 100.")), ("interval_ms", S("integer", "Delay between repeats from 0 to 5000 ms."))), ["key"]),
        Tool("key_down", "Hold one explicit key down across subsequent actions. Set desktop=true for the current foreground focus. Held keys are automatically released by key_up, end_session, or broker disposal.",
            WindowProps(("desktop", S("boolean", "Send to the current foreground focus without a window selector or activation.")), ("key", S("string", "Explicit key to hold, such as shift, ctrl, alt, space, left, or w."))), ["key"]),
        Tool("key_up", "Release one key previously held with key_down, optionally against the current foreground with desktop=true.",
            WindowProps(("desktop", S("boolean", "Send to the current foreground focus without a window selector or activation.")), ("key", S("string", "Explicit named key to release."))), ["key"]),
        Tool("type_text", "Type arbitrary Unicode text with SendInput into either a selected window's focused control or the unchanged current foreground focus when desktop=true.",
            WindowProps(("desktop", S("boolean", "Send to the current foreground focus without a window selector or activation.")), ("text", S("string", "Text to type."))), ["text"]),
        Tool("scroll", "Scroll vertically or horizontally at a window, direct-screen, or desktop-screenshot point using SendInput.",
            WindowProps(("x", S("integer", "X coordinate, window center by default.")), ("y", S("integer", "Y coordinate, window center by default.")), ("coordinate_space", Enum("window", "screen", "screenshot")), ("relative", S("boolean", "Legacy window-relative flag, default true.")), ("vertical", S("integer", "Wheel notches; positive up, negative down.")), ("horizontal", S("integer", "Wheel notches; positive right, negative left.")), ("screenshot_id", S("string", "Bind coordinates to a recent capture/snapshot.")), ("max_age_ms", S("integer", "Maximum screenshot age, default 15000 ms.")))),
        Tool("drag", "Perform a self-contained drag with a selected mouse button between window, direct-screen, or desktop-screenshot points.",
            WindowProps(("from_x", S("integer", "Start X.")), ("from_y", S("integer", "Start Y.")), ("to_x", S("integer", "End X.")), ("to_y", S("integer", "End Y.")), ("coordinate_space", Enum("window", "screen", "screenshot")), ("relative", S("boolean", "Legacy window-relative flag, default true.")), ("duration_ms", S("integer", "Drag duration from 0 to 10000 ms.")), ("button", Enum("left", "right", "middle", "x1", "x2")), ("screenshot_id", S("string", "Bind coordinates to a recent capture/snapshot.")), ("max_age_ms", S("integer", "Maximum screenshot age, default 15000 ms."))), ["from_x", "from_y", "to_x", "to_y"]),
        Tool("set_window_state", "Explicitly minimize, maximize, or restore one window and verify the resulting native state.",
            WindowProps(("state", Enum("minimize", "maximize", "restore")), ("timeout_ms", S("integer", "Verification timeout up to 10000 ms."))), ["state"]),
        Tool("set_window_bounds", "Move and resize one window to an exact physical virtual-desktop rectangle through Win32, restoring it first when minimized/maximized and verifying the readback.",
            WindowProps(("x", S("integer", "Physical virtual-desktop left coordinate; negative values are valid.")), ("y", S("integer", "Physical virtual-desktop top coordinate; negative values are valid.")), ("width", S("integer", "Positive physical outer-window width.")), ("height", S("integer", "Positive physical outer-window height.")), ("activate", S("boolean", "Activate after moving; default false preserves the current foreground.")), ("timeout_ms", S("integer", "Verification timeout up to 10000 ms."))), ["x", "y", "width", "height"]),
        Tool("activate_window", "Restore and bring exactly one selected window to the foreground.", WindowProps()),
        Tool("end_session", "Clear cached UIA elements and explicitly end the current control session.", Props())
    ];

    private static ToolDefinition Tool(string name, string description, object properties, string[]? required = null)
    {
        var schema = new Dictionary<string, object> { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required is { Length: > 0 }) schema["required"] = required;
        return new ToolDefinition(name, description, schema, new { readOnlyHint = name is "list_windows" or "display_info" or "pointer_position" or "window_from_point" or "wait_for_window" or "inspect_window" or "observe_changes" or "find_controls" or "wait_for_ui" or "wait_for_visual_change" or "wait_for_visual_stable" or "compare_screenshots" or "capture" or "capture_region" or "observe_desktop" or "snapshot" or "ocr" or "find_text" or "find_image" or "read_clipboard_text", destructiveHint = false, openWorldHint = name == "launch_app" });
    }

    private static Dictionary<string, object> WindowProps(params (string Name, object Schema)[] extra)
    {
        var properties = Props(
            ("window_id", S("integer", "Exact window id returned by list_windows.")),
            ("title", S("string", "Title substring; only use when it resolves uniquely.")),
            ("app", S("string", "App/process substring; only use when it resolves uniquely.")));
        foreach (var (name, schema) in extra) properties[name] = schema;
        return properties;
    }

    private static Dictionary<string, object> QueryProps(params (string Name, object Schema)[] extra)
    {
        var properties = WindowProps(
            ("name", S("string", "Exact accessible name.")),
            ("name_contains", S("string", "Accessible name substring.")),
            ("automation_id", S("string", "Exact UIA AutomationId.")),
            ("control_type", S("string", "UIA control type, such as Button or Edit.")),
            ("class_name", S("string", "Native/UI class substring.")),
            ("enabled_only", S("boolean", "Only match enabled controls.")),
            ("scan_limit", S("integer", "Maximum UIA elements to scan.")));
        foreach (var (name, schema) in extra) properties[name] = schema;
        return properties;
    }

    private static Dictionary<string, object> Props(params (string Name, object Schema)[] values) =>
        values.ToDictionary(item => item.Name, item => item.Schema, StringComparer.Ordinal);

    private static object S(string type, string description) => new { type, description };
    private static object Enum(params string[] values) => new { type = "string", @enum = values };
}
