# MCP tool reference

## Target selectors

Every window-scoped tool accepts `window_id`, `title`, or `app`. Prefer the exact numeric `window_id` returned by `list_windows`. Title and app substrings must resolve to exactly one window or the broker rejects the action.

`observe_desktop` is the whole-screen starting observation. It returns one image plus `topology`, `windows`, `pointer`, and `capture` metadata under a single read-side UI lock. The capture id is a standard virtual-desktop screenshot id: pass its image-relative coordinates to pointer/mouse tools with `coordinate_space=screenshot` and no window selector. `include_untitled=true` expands the top-level window list without changing the image.

`window_from_point` takes physical screen `x/y`, calls `WindowFromPoint`, and returns the actual `native_child_window_id`, class, optional title, plus its `GetAncestor(GA_ROOT)` top-level `window`. It is a pure hit test: it does not move the pointer, activate, or click. Convert image-relative desktop coordinates to physical coordinates by adding the desktop capture bounds origin before calling it.

Semantic queries accept `control_id`, `name`, `name_contains`, `automation_id`, `control_type`, `class_name`, `enabled_only`, and `scan_limit`. Prefer `control_id`; use query fields to discover or recover.

## Tools

| Tool | Purpose |
| --- | --- |
| `list_windows` | Return visible top-level windows, process identity, bounds, class, owner/root-owner links, and state. |
| `display_info` | Return physical virtual-desktop and per-monitor bounds, work areas, effective DPI, primary flag, and scale. |
| `pointer_position` | Return the current pointer position in physical virtual-desktop screen pixels. |
| `window_from_point` | Map a physical point to its actual child HWND/class/title and stable top-level root window without input. |
| `launch_app` | Launch an executable, registered app, file, or URI. |
| `wait_for_window` | Wait for a top-level/owned window to exist or disappear by title, app, class, process, or owner ids. |
| `inspect_window` | Return the UIA3 tree, controls, stable ids, patterns, state, and physical bounds. |
| `observe_changes` | Compare against a cached observation id and return only added, removed, or changed controls. |
| `find_controls` | Filter controls by semantic properties. |
| `invoke` | Invoke, select, toggle, expand, or center-click one semantic control. |
| `perform_secondary_action` | Explicitly focus/raise, select, add/remove selection, toggle, expand/collapse, or UIA-scroll one semantic control. |
| `enter_text` | Prefer UIA ValuePattern; fall back to focus, select-all, and Unicode SendInput. |
| `paste_text` | Preserve all direct clipboard formats, focus a semantic target, replace or append through real Ctrl+V, verify UIA Value when exposed, and restore on success or failure. |
| `copy_text` | Preserve all direct clipboard formats, focus a semantic target, copy the current selection or select-all through real Ctrl+C, return Unicode text, and restore on success or failure. |
| `wait_for_ui` | Wait for existence/visibility/focus, Value equality/containment, selected/unselected, toggle on/off/indeterminate, expanded/collapsed, or read-only/editable state. |
| `wait_for_visual_change` | Re-capture the exact source of a fresh screenshot until its PNG hash changes, returning the latest actionable PNG on match or timeout. |
| `wait_for_visual_stable` | Re-capture the same source until its exact PNG hash remains unchanged continuously for a requested interval, returning the latest actionable PNG. |
| `compare_screenshots` | Compare same-source cached PNGs and return exact changed-pixel metrics plus localized tile-connected bounds. |
| `capture` | Return PNG image content plus an actionable screenshot id for a window or virtual desktop, and optionally save it. |
| `capture_region` | Crop exact cached screenshot pixels or a fresh window/desktop frame and return a physically actionable region PNG. |
| `observe_desktop` | Atomically return one virtual-desktop PNG plus topology, visible windows, pointer position, and actionable screenshot metadata. |
| `snapshot` | Atomically return UIA state plus a fresh image with screenshot id, timestamp, and SHA-256. |
| `ocr` | Recognize exact cached screenshot pixels, an existing image, or a fresh window/desktop capture with Windows.Media.Ocr. |
| `find_text` | Search exact cached screenshot pixels or a fresh window/desktop frame and return actionable OCR bounds/centers. |
| `find_image` | Search exact cached screenshot pixels or a fresh frame for a local template, returning same-frame scale, bounds, center, and id. |
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
| `set_window_bounds` | Move/resize an exact window to a verified physical virtual-desktop rectangle, preserving foreground by default. |
| `activate_window` | Restore and foreground one exact window. |
| `end_session` | Release tracked mouse buttons and keys, discard unused clipboard backup ids, clear element caches, and end the logical control session. |

Controls include `parentId`, `depth`, `childCount`, `value`, `isReadOnly`, `isSelected`, `toggleState`, `expandCollapseState`, and horizontal/vertical scroll percentages when their UIA patterns are supported. Window observations also return `focusedControlId`, `documentText`, `selectedText`, and `selectedControlIds`. An `observationId` remains available for the latest 16 inspected/snapshotted states in a session; `observe_changes` compares these semantic states as well as identity, layout, focus, and patterns.

Coordinate tools accept `coordinate_space` (`window`, `screen`, or `screenshot`), `screenshot_id`, and `max_age_ms` (default 15000). Window screenshot coordinates map from the DWM-visible frame; desktop screenshot coordinates map from the physical virtual-desktop origin, including negative monitor origins. A bound action is rejected before input if the id is unknown/expired, belongs to another target, a window moved/resized, or display topology changed. Semantic and input mutations invalidate older screenshot ids. Window click/scroll/drag actions return `window-and-screenshot-reobserve`; desktop-bound actions avoid foreground activation and return `desktop-screenshot-reobserve`; direct screen actions need no selector and return `screen-input-and-desktop-reobserve`. Mouse down/up return tracked held-button state and should finish through explicit screen coordinates after the initiating screenshot becomes invalid.

Screenshot-bound `click`, `scroll`, and self-contained `drag` also return `data.visual_diff`. When source geometry is unchanged, it compares the exact original full/cropped pixels with the post-action capture at zero channel threshold and reports changed pixels/fraction, union bounds, channel delta, and at most 20 tile regions. A changed window/desktop extent reports `comparable=false` plus bounds and does not overturn the completed input. Unbound window actions return `visual_diff=null`; direct-screen actions have no trusted before frame.

`capture_region` takes required `x/y/width/height` in source-image pixels, not physical screen coordinates. With `screenshot_id`, it crops the exact cached PNG and rejects `desktop=true` or any window selector; `max_age_ms` defaults to 15000. Without an id, it captures a fresh selected window or desktop first. The rectangle must be positive and fully contained, including when the source itself is a prior crop. Its returned PNG `bounds` are physical; screenshot coordinate `(0,0)` maps to that cropped physical origin. Nested crop offsets are accumulated against the original full source, so desktop topology/window geometry validation, screenshot-space input, and same-region visual waits remain correct. A visual-wait result inherits the region and can be chained into another wait or screenshot-bound input.

`ocr` and `find_text` also accept a fresh cached `screenshot_id`, including a cropped or nested-cropped id. They recognize those exact PNG bytes, revalidate the original window/desktop identity and `max_age_ms`, and return the same screenshot id/bounds so `find_text` centers remain directly actionable. Omit all selectors when using an id. `ocr path` is mutually exclusive with an id and selectors and returns recognition only; without an id/path, both tools preserve their fresh window/desktop capture behavior.

State-changing tools return `backend` and `verification`. `uia3-reobserve` means the control was found again after the action. `window-reobserve-element-changed` means the action completed and the prior element intentionally disappeared or changed identity.

Visual tools reject minimized windows because WGC/PrintWindow output is not dependable in that state. Call `set_window_state` with `restore`, wait for the verified result, and observe again. Use `ownerWindowId`/`rootOwnerWindowId` from `list_windows` or `wait_for_window` to keep transient dialogs associated with the intended main window.

An exact `window_id` is resolved directly as an HWND even when that window is temporarily hidden or untitled. If an app destroys and recreates the HWND, the cached id follows it only when the same process id, native class, and non-empty title identify one unique replacement. Otherwise the broker returns an explicit stale-id error; call `list_windows`/`wait_for_window` and select the replacement rather than guessing.

`find_image` accepts the same fresh cached `screenshot_id` contract, including cropped/nested regions, and rejects selectors while an id is present. It validates `max_age_ms` plus original geometry/topology and searches the exact cached PNG in memory, returning the unchanged id/hash/bounds. Without an id it fresh-captures the selected window/desktop. Matching defaults to `scale_min=scale_max=1.0`; for known DPI or zoom variation, scales are bounded to 0.25-4.0 with a 0.01-1.0 step and at most 25 evaluated sizes. Results are ranked and overlap-suppressed across scales. Use a narrow range because runtime grows with every evaluated size, and use `snapshot`/model vision for novel or rotated targets.

`write_clipboard_text` defaults to `preserve_previous=true`. It materializes every direct clipboard format before mutation and fails without changing the clipboard if one cannot be backed up safely. Keep the returned `backup_id` and call `restore_clipboard` in cleanup after the paste; the restore tolerates Windows line-ending normalization, verifies the original format set and normalized text digest, and consumes the id. Backup ids belong to the current broker process and are discarded by `end_session`/shutdown without automatically changing the current clipboard. Use `preserve_previous=false` only for an intentional persistent clipboard update.

`paste_text` wraps that lifecycle into one transaction. Release any tracked held keys first so they cannot change its chords. It focuses one exact semantic control, uses `Ctrl+A` for replacement or `Ctrl+End` for append, sends real `Ctrl+V`, and keeps the temporary clipboard active until the target's UIA Value equals the expected text or `timeout_ms` expires. Controls without Value state use `settle_ms` followed by semantic re-observation. It restores before returning or rethrowing an action failure; if restoration itself cannot be verified, the error includes the still-valid `backup_id` for an explicit `restore_clipboard` retry.

`copy_text` also requires released tracked keys. With `selection=current` it preserves the target's selection; `selection=all` sends `Ctrl+A`. It seeds a unique temporary marker, sends `Ctrl+C`, waits for `GetClipboardSequenceNumber` to change, reads Unicode text, compares it with UIA selected text when exposed, then restores the original formats before returning. A copy that publishes no text or never changes the clipboard fails and still runs restoration. Restore verification requires the original content, format set, and sequence to remain stable for 150 ms so delayed clipboard rendering cannot escape after the tool returns.

For `wait_for_ui`, use `expected_value` with `value_equals` or `value_contains`; comparison is case-insensitive unless `case_sensitive=true`. Exact `control_id` selectors reuse validated element locators, while other selectors use targeted traversal and every poll re-resolves the target HWND. A UIA provider call itself cannot be cancelled inside its native process, but the MCP transport deadline can replace that process; an observation completed after `timeout_ms` is never reported as a successful match.

`wait_for_visual_change` requires a cached `screenshot_id` and automatically reuses its window or virtual-desktop source; do not add a selector. The source must be within `max_age_ms` when the wait begins. Every poll verifies that the window id/bounds or virtual-desktop topology still matches, then compares the exact encoded PNG SHA-256. A match completed after `timeout_ms` is reported as `matched=false`; both outcomes include a fresh actionable `capture`. Prefer semantic waits when possible because any animation, caret, clock, or unrelated desktop pixel can change the exact hash.

`wait_for_visual_stable` uses the same source validation but starts a new observed stability interval with its first fresh capture. Each changed hash resets that interval; `stable=true` requires at least `stable_ms` of continuously identical fresh samples before the deadline. Timeout returns `stable=false`, `stableForMs`, the sample count, and the latest capture. Clocks, carets, video, notifications, and other dynamic pixels can prevent exact full-frame stability, so use semantic state when available.

`compare_screenshots` requires fresh `before_screenshot_id` and `after_screenshot_id` values with identical window/desktop identity, geometry/topology, full-source bounds, public capture bounds, and crop identity. `channel_threshold=0` compares every BGRA channel exactly; a pixel changes if any channel delta exceeds the configured 0-255 threshold. `changedImageBounds`/`changedScreenBounds` are pixel-exact union bounds. `regions` are four-neighbor groups of `tile_size` 8-128 blocks, so their bounds are localization hints and may include unchanged pixels; `max_regions` caps output at 200 and reports omissions. The tool only reads cached PNGs.

`press_key` interprets printable characters using the active Windows keyboard layout, including implied modifier bits (for example `A` supplies Shift); use `plus` because `+` separates chord tokens. `key_down`/`key_up` require explicit names, so hold `shift` separately before a modified printable key. Mouse buttons use canonical `left`, `right`, `middle`, `x1`, and `x2` names. A self-contained click/drag rejects a button already held by `mouse_down`; continue that gesture with `move_pointer` and finish with `mouse_up`. Any tracked mouse buttons and keys still held at `end_session` or broker disposal are released automatically.

`press_key`, `key_down`, `key_up`, and `type_text` accept `desktop=true` for built-in-computer-use-style current-focus input. The broker records the current foreground HWND but deliberately does not activate it; `window_id`, `title`, or `app` combined with desktop mode is rejected before SendInput. Window mode remains safer when the destination is known. Global `key_up` still sends the release if the desktop temporarily has no foreground window, preventing a held key from becoming stranded.

`set_window_bounds` takes required `x`, `y`, `width`, and `height` for the outer Win32 window rectangle in physical virtual-desktop pixels. Width/height must be positive; x/y may be negative. A minimized or maximized target is restored first. The default `activate=false` uses `SWP_NOACTIVATE`; `activate=true` performs the same verified foreground activation as `activate_window`. The call succeeds only after `GetWindowRect` exactly equals the requested rectangle.
