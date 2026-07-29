# MCP tool reference

## Target selectors

Every window-scoped tool accepts `window_id`, `title`, or `app`. Prefer the exact numeric `window_id` returned by `list_windows`. Title and app substrings must resolve to exactly one window or the broker rejects the action.

Semantic queries accept `control_id`, `name`, `name_contains`, `automation_id`, `control_type`, `class_name`, `enabled_only`, and `scan_limit`. Prefer `control_id`; use query fields to discover or recover.

## Tools

| Tool | Purpose |
| --- | --- |
| `list_windows` | Return visible top-level windows, process identity, bounds, class, owner/root-owner links, and state. |
| `display_info` | Return physical virtual-desktop and per-monitor bounds, work areas, effective DPI, primary flag, and scale. |
| `pointer_position` | Return the current pointer position in physical virtual-desktop screen pixels. |
| `launch_app` | Launch an executable, registered app, file, or URI. |
| `wait_for_window` | Wait for a top-level/owned window to exist or disappear by title, app, class, process, or owner ids. |
| `inspect_window` | Return the UIA3 tree, controls, stable ids, patterns, state, and physical bounds. |
| `observe_changes` | Compare against a cached observation id and return only added, removed, or changed controls. |
| `find_controls` | Filter controls by semantic properties. |
| `invoke` | Invoke, select, toggle, expand, or center-click one semantic control. |
| `perform_secondary_action` | Explicitly focus/raise, select, add/remove selection, toggle, expand/collapse, or UIA-scroll one semantic control. |
| `enter_text` | Prefer UIA ValuePattern; fall back to focus, select-all, and Unicode SendInput. |
| `wait_for_ui` | Wait for existence/visibility/focus, Value equality/containment, selected/unselected, toggle on/off/indeterminate, expanded/collapsed, or read-only/editable state. |
| `capture` | Return PNG image content for a window or virtual desktop and optionally save it. |
| `snapshot` | Atomically return UIA state plus a fresh image with screenshot id, timestamp, and SHA-256. |
| `ocr` | Recognize an existing image or fresh capture with Windows.Media.Ocr. |
| `find_text` | Fresh-capture a window and return matching OCR line/word bounds, centers, and screenshot id. |
| `find_image` | Fresh-capture a window and locate an exact-scale local PNG/JPEG template, returning scored screenshot/screen bounds, centers, and screenshot id. |
| `move_pointer` | Move or smoothly hover in screen/window/screenshot coordinates without clicking or foreground activation. |
| `click` | Click window-relative or screen coordinates with left, right, or middle button. |
| `press_key` | Press/release a `+`-separated chord with implied printable modifiers and optional repeat/interval timing. Covers navigation, F1-F24, left/right modifiers, numpad, browser, media, and volume keys. |
| `key_down` | Hold one explicit named key across later actions and track it for guaranteed cleanup. |
| `key_up` | Release one explicitly held key. |
| `type_text` | Type arbitrary Unicode into the focused control. |
| `scroll` | Send vertical and horizontal wheel input at a selected point. |
| `drag` | Drag between physical points with a configurable duration. |
| `set_window_state` | Minimize, maximize, or restore one window and verify native state. |
| `activate_window` | Restore and foreground one exact window. |
| `end_session` | Clear element caches and end the logical control session. |

Controls include `parentId`, `depth`, `childCount`, `value`, `isReadOnly`, `isSelected`, `toggleState`, `expandCollapseState`, and horizontal/vertical scroll percentages when their UIA patterns are supported. Window observations also return `focusedControlId`, `documentText`, `selectedText`, and `selectedControlIds`. An `observationId` remains available for the latest 16 inspected/snapshotted states in a session; `observe_changes` compares these semantic states as well as identity, layout, focus, and patterns.

Coordinate tools accept `coordinate_space` (`window`, `screen`, or `screenshot`), `screenshot_id`, and `max_age_ms` (default 15000). Screenshot coordinates are mapped from the capture's DWM-visible screen origin. A bound action is rejected before input if the id is unknown/expired, belongs to another window, or the target moved/resized. Semantic and input mutations invalidate older screenshot ids. Pixel actions return a post-action screenshot id and `window-and-screenshot-reobserve` verification.

State-changing tools return `backend` and `verification`. `uia3-reobserve` means the control was found again after the action. `window-reobserve-element-changed` means the action completed and the prior element intentionally disappeared or changed identity.

Visual tools reject minimized windows because WGC/PrintWindow output is not dependable in that state. Call `set_window_state` with `restore`, wait for the verified result, and observe again. Use `ownerWindowId`/`rootOwnerWindowId` from `list_windows` or `wait_for_window` to keep transient dialogs associated with the intended main window.

An exact `window_id` is resolved directly as an HWND even when that window is temporarily hidden or untitled. If an app destroys and recreates the HWND, the cached id follows it only when the same process id, native class, and non-empty title identify one unique replacement. Otherwise the broker returns an explicit stale-id error; call `list_windows`/`wait_for_window` and select the replacement rather than guessing.

For `wait_for_ui`, use `expected_value` with `value_equals` or `value_contains`; comparison is case-insensitive unless `case_sensitive=true`. Exact `control_id` selectors reuse validated element locators, while other selectors use targeted traversal and every poll re-resolves the target HWND. A UIA provider call itself cannot be preempted, but an observation completed after `timeout_ms` is not reported as a successful match.

`press_key` interprets printable characters using the active Windows keyboard layout, including implied modifier bits (for example `A` supplies Shift); use `plus` because `+` separates chord tokens. `key_down`/`key_up` require explicit names, so hold `shift` separately before a modified printable key. Any tracked key still held at `end_session` or broker disposal is released automatically.
