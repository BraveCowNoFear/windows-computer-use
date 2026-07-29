# MCP tool reference

## Target selectors

Every window-scoped tool accepts `window_id`, `title`, or `app`. Prefer the exact numeric `window_id` returned by `list_windows`. Title and app substrings must resolve to exactly one window or the broker rejects the action.

Semantic queries accept `control_id`, `name`, `name_contains`, `automation_id`, `control_type`, `class_name`, `enabled_only`, and `scan_limit`. Prefer `control_id`; use query fields to discover or recover.

## Tools

| Tool | Purpose |
| --- | --- |
| `list_windows` | Return visible top-level windows, process identity, bounds, foreground, and minimized state. |
| `launch_app` | Launch an executable, registered app, file, or URI. |
| `inspect_window` | Return the UIA3 tree, controls, stable ids, patterns, state, and physical bounds. |
| `find_controls` | Filter controls by semantic properties. |
| `invoke` | Invoke, select, toggle, expand, or center-click one semantic control. |
| `enter_text` | Prefer UIA ValuePattern; fall back to focus, select-all, and Unicode SendInput. |
| `wait_for_ui` | Wait for `exists`, `absent`, `visible`, `hidden`, `enabled`, or `focused`. |
| `capture` | Return PNG image content for a window or virtual desktop and optionally save it. |
| `snapshot` | Atomically return UIA state plus a fresh image with screenshot id, timestamp, and SHA-256. |
| `ocr` | Recognize an existing image or fresh capture with Windows.Media.Ocr. |
| `click` | Click window-relative or screen coordinates with left, right, or middle button. |
| `press_key` | Send a `+`-separated chord such as `ctrl+s`, `alt+f4`, or `shift+tab`. |
| `type_text` | Type arbitrary Unicode into the focused control. |
| `scroll` | Send vertical and horizontal wheel input at a selected point. |
| `drag` | Drag between physical points with a configurable duration. |
| `activate_window` | Restore and foreground one exact window. |
| `end_session` | Clear element caches and end the logical control session. |

Coordinate tools accept `screenshot_id` and `max_age_ms` (default 15000). A bound action is rejected before input if the id is unknown/expired, belongs to another window, or the target moved/resized. Pixel actions return a post-action screenshot id and `window-and-screenshot-reobserve` verification.

State-changing tools return `backend` and `verification`. `uia3-reobserve` means the control was found again after the action. `window-reobserve-element-changed` means the action completed and the prior element intentionally disappeared or changed identity.
