using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Mcp;

public static class ToolCatalog
{
    public static IReadOnlyList<ToolDefinition> All { get; } =
    [
        Tool("list_windows", "List visible top-level Windows windows with native class and owner/root-owner relationships. Always use a returned window_id instead of guessing a target.",
            Props(("include_untitled", S("boolean", "Include visible titleless top-level windows, default false.")))),
        Tool("display_info", "Return physical virtual-desktop bounds plus every monitor's bounds, work area, primary flag, effective DPI, and scale percentage.", Props()),
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
        Tool("enter_text", "Set or type Unicode text into a semantic control, preferring UIA ValuePattern and falling back to SendInput.",
            QueryProps(("control_id", S("string", "Stable control id.")), ("text", S("string", "Text to enter.")), ("append", S("boolean", "Append instead of replacing."))), ["text"]),
        Tool("wait_for_ui", "Poll UIA until a control exists, disappears, becomes visible, enabled, or focused.",
            QueryProps(("state", Enum("exists", "absent", "visible", "hidden", "enabled", "focused")), ("timeout_ms", S("integer", "Timeout up to 120000 ms.")), ("poll_ms", S("integer", "Polling interval.")))),
        Tool("capture", "Capture a window through Windows Graphics Capture with PrintWindow/screen-copy fallback, or capture the virtual desktop, and return PNG image content.",
            WindowProps(("desktop", S("boolean", "Capture the full virtual desktop instead of one window.")), ("path", S("string", "Optional absolute output path.")))),
        Tool("snapshot", "Return one atomic computer-use observation containing the UIA semantic state and a fresh window image with screenshot id, timestamp, and content hash.",
            WindowProps(("limit", S("integer", "Maximum UIA elements, default 400.")), ("path", S("string", "Optional absolute output path.")))),
        Tool("ocr", "Run Windows.Media.Ocr on a window, desktop, or existing PNG using installed Windows language packs.",
            WindowProps(("desktop", S("boolean", "OCR the virtual desktop.")), ("path", S("string", "Existing image path; when omitted a fresh capture is used.")), ("language", S("string", "Optional BCP-47 language tag.")))),
        Tool("find_text", "Capture one window, run Windows OCR, and return matching line/word bounds plus a screenshot id for coordinate_space=screenshot actions.",
            WindowProps(("text", S("string", "OCR text to locate.")), ("match", Enum("exact", "contains")), ("case_sensitive", S("boolean", "Use ordinal case-sensitive matching.")), ("language", S("string", "Optional BCP-47 language tag.")), ("limit", S("integer", "Maximum matches, default 50."))), ["text"]),
        Tool("click", "Click physical pixels in a selected window using SendInput. Coordinates are window-relative by default.",
            WindowProps(("x", S("integer", "X coordinate.")), ("y", S("integer", "Y coordinate.")), ("coordinate_space", Enum("window", "screen", "screenshot")), ("relative", S("boolean", "Legacy window-relative flag, default true; coordinate_space takes precedence.")), ("button", Enum("left", "right", "middle")), ("count", S("integer", "Click count 1-4.")), ("screenshot_id", S("string", "Bind the coordinates to a recent capture/snapshot; required for screenshot coordinates.")), ("max_age_ms", S("integer", "Maximum screenshot age, default 15000 ms."))), ["x", "y"]),
        Tool("press_key", "Press a + separated key chord in a selected window, such as ctrl+s or alt+f4.",
            WindowProps(("key", S("string", "Key or chord."))), ["key"]),
        Tool("type_text", "Type arbitrary Unicode text into the currently focused control of a selected window with SendInput.",
            WindowProps(("text", S("string", "Text to type."))), ["text"]),
        Tool("scroll", "Scroll vertically or horizontally at a point in a selected window using SendInput.",
            WindowProps(("x", S("integer", "X coordinate, window center by default.")), ("y", S("integer", "Y coordinate, window center by default.")), ("coordinate_space", Enum("window", "screen", "screenshot")), ("relative", S("boolean", "Legacy window-relative flag, default true.")), ("vertical", S("integer", "Wheel notches; positive up, negative down.")), ("horizontal", S("integer", "Wheel notches; positive right, negative left.")), ("screenshot_id", S("string", "Bind coordinates to a recent capture/snapshot.")), ("max_age_ms", S("integer", "Maximum screenshot age, default 15000 ms.")))),
        Tool("drag", "Drag with the left mouse button between two points in a selected window.",
            WindowProps(("from_x", S("integer", "Start X.")), ("from_y", S("integer", "Start Y.")), ("to_x", S("integer", "End X.")), ("to_y", S("integer", "End Y.")), ("coordinate_space", Enum("window", "screen", "screenshot")), ("relative", S("boolean", "Legacy window-relative flag, default true.")), ("duration_ms", S("integer", "Drag duration.")), ("screenshot_id", S("string", "Bind coordinates to a recent capture/snapshot.")), ("max_age_ms", S("integer", "Maximum screenshot age, default 15000 ms."))), ["from_x", "from_y", "to_x", "to_y"]),
        Tool("set_window_state", "Explicitly minimize, maximize, or restore one window and verify the resulting native state.",
            WindowProps(("state", Enum("minimize", "maximize", "restore")), ("timeout_ms", S("integer", "Verification timeout up to 10000 ms."))), ["state"]),
        Tool("activate_window", "Restore and bring exactly one selected window to the foreground.", WindowProps()),
        Tool("end_session", "Clear cached UIA elements and explicitly end the current control session.", Props())
    ];

    private static ToolDefinition Tool(string name, string description, object properties, string[]? required = null)
    {
        var schema = new Dictionary<string, object> { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required is { Length: > 0 }) schema["required"] = required;
        return new ToolDefinition(name, description, schema, new { readOnlyHint = name is "list_windows" or "display_info" or "wait_for_window" or "inspect_window" or "observe_changes" or "find_controls" or "capture" or "snapshot" or "ocr" or "find_text", destructiveHint = false, openWorldHint = name == "launch_app" });
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
