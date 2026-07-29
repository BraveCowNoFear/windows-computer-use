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
| `capture` | Return PNG image content plus an actionable screenshot id for a window or virtual desktop, and optionally save it. |
| `snapshot` | Atomically return UIA state plus a fresh image with screenshot id, timestamp, and SHA-256. |
| `ocr` | Recognize an existing image or fresh window/desktop capture with Windows.Media.Ocr; fresh captures are screenshot-bound. |
| `find_text` | Fresh-capture a window or desktop and return matching OCR line/word bounds, centers, and actionable screenshot id. |
| `find_image` | Fresh-capture a window or desktop and locate a local PNG/JPEG template at exact scale by default or across a bounded scale range, returning score, matched scale, screenshot/screen bounds, center, and screenshot id. |
| `read_clipboard_text` | Read current Unicode text, length, raw/normalized SHA-256 digests, and direct clipboard format names. |
| `write_clipboard_text` | Replace all current formats with Unicode text and optionally return a session-local backup id after materializing every direct format. |
| `restore_clipboard` | Restore and verify the formats/text captured by one backup id, then consume that id. |
| `move_pointer` | Move or smoothly hover in screen/window/screenshot coordinates without clicking or foreground activation. |
| `click` | Click window-relative, screen, or screenshot coordinates with left, right, middle, X1, or X2 button. |
| `mouse_down` | Move to a point, hold one of five mouse buttons across later actions, and track it for guaranteed cleanup. |
| `mouse_up` | Move to a point and release one explicitly held mouse button. |
| `press_key` | Press/release a `+`-separated chord with implied printable modifiers and optional repeat/interval timing. Covers navigation, F1-F24, left/right modifiers, numpad, browser, media, and volume keys. |
| `key_down` | Hold one explicit named key across later actions and track it for guaranteed cleanup. |
| `key_up` | Release one explicitly held key. |
| `type_text` | Type arbitrary Unicode into the focused control. |
| `scroll` | Send vertical and horizontal wheel input at a selected point. |
| `drag` | Perform a self-contained left/right/middle/X1/X2 drag between physical points with configurable duration. |
| `set_window_state` | Minimize, maximize, or restore one window and verify native state. |
| `activate_window` | Restore and foreground one exact window. |
| `end_session` | Release tracked mouse buttons and keys, discard unused clipboard backup ids, clear element caches, and end the logical control session. |

Controls include `parentId`, `depth`, `childCount`, `value`, `isReadOnly`, `isSelected`, `toggleState`, `expandCollapseState`, and horizontal/vertical scroll percentages when their UIA patterns are supported. Window observations also return `focusedControlId`, `documentText`, `selectedText`, and `selectedControlIds`. An `observationId` remains available for the latest 16 inspected/snapshotted states in a session; `observe_changes` compares these semantic states as well as identity, layout, focus, and patterns.

Coordinate tools accept `coordinate_space` (`window`, `screen`, or `screenshot`), `screenshot_id`, and `max_age_ms` (default 15000). Window screenshot coordinates map from the DWM-visible frame; desktop screenshot coordinates map from the physical virtual-desktop origin, including negative monitor origins. A bound action is rejected before input if the id is unknown/expired, belongs to another target, a window moved/resized, or display topology changed. Semantic and input mutations invalidate older screenshot ids. Window click/scroll/drag actions return `window-and-screenshot-reobserve`; desktop-bound actions avoid foreground activation and return `desktop-screenshot-reobserve`; direct screen actions need no selector and return `screen-input-and-desktop-reobserve`. Mouse down/up return tracked held-button state and should finish through explicit screen coordinates after the initiating screenshot becomes invalid.

State-changing tools return `backend` and `verification`. `uia3-reobserve` means the control was found again after the action. `window-reobserve-element-changed` means the action completed and the prior element intentionally disappeared or changed identity.

Visual tools reject minimized windows because WGC/PrintWindow output is not dependable in that state. Call `set_window_state` with `restore`, wait for the verified result, and observe again. Use `ownerWindowId`/`rootOwnerWindowId` from `list_windows` or `wait_for_window` to keep transient dialogs associated with the intended main window.

An exact `window_id` is resolved directly as an HWND even when that window is temporarily hidden or untitled. If an app destroys and recreates the HWND, the cached id follows it only when the same process id, native class, and non-empty title identify one unique replacement. Otherwise the broker returns an explicit stale-id error; call `list_windows`/`wait_for_window` and select the replacement rather than guessing.

`find_image` defaults to `scale_min=scale_max=1.0`. For known DPI or zoom variation, scales are bounded to 0.25-4.0 with a 0.01-1.0 step and at most 25 evaluated sizes; 1.0 is included when it lies inside the range. Results are ranked and overlap-suppressed across scales. Use a narrow range because runtime grows with every evaluated size, and use `snapshot`/model vision for novel or rotated targets.

`write_clipboard_text` defaults to `preserve_previous=true`. It materializes every direct clipboard format before mutation and fails without changing the clipboard if one cannot be backed up safely. Keep the returned `backup_id` and call `restore_clipboard` in cleanup after the paste; the restore tolerates Windows line-ending normalization, verifies the original format set and normalized text digest, and consumes the id. Backup ids belong to the current broker process and are discarded by `end_session`/shutdown without automatically changing the current clipboard. Use `preserve_previous=false` only for an intentional persistent clipboard update.

For `wait_for_ui`, use `expected_value` with `value_equals` or `value_contains`; comparison is case-insensitive unless `case_sensitive=true`. Exact `control_id` selectors reuse validated element locators, while other selectors use targeted traversal and every poll re-resolves the target HWND. A UIA provider call itself cannot be preempted, but an observation completed after `timeout_ms` is not reported as a successful match.

`press_key` interprets printable characters using the active Windows keyboard layout, including implied modifier bits (for example `A` supplies Shift); use `plus` because `+` separates chord tokens. `key_down`/`key_up` require explicit names, so hold `shift` separately before a modified printable key. Mouse buttons use canonical `left`, `right`, `middle`, `x1`, and `x2` names. A self-contained click/drag rejects a button already held by `mouse_down`; continue that gesture with `move_pointer` and finish with `mouse_up`. Any tracked mouse buttons and keys still held at `end_session` or broker disposal are released automatically.
