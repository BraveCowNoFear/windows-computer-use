# Recovery guide

## Stale or missing semantic elements

Re-run `inspect_window`, then search by `automation_id`, exact `name`, and `control_type`. A cached `control_id` automatically attempts re-location from those properties. If duplicates remain, narrow the query or inspect a transient child window.

If the broker says a `window_id` itself is stale, Windows has destroyed that HWND and no unique same-process/class/title replacement was found. Re-run `list_windows` or `wait_for_window` and select the replacement. Temporary hiding, an empty title, and minimize/restore do not by themselves invalidate an exact HWND selector.

## Custom canvas or empty UIA tree

Use `find_text` when the target has readable text, `find_image` when you have an exact-scale local PNG/JPEG template, or snapshot the selected window for model-side vision. Use image-relative coordinates with `coordinate_space=screenshot` and pass the same `screenshot_id`. If template matching fails after a DPI/zoom change, capture a template at the current scale instead of lowering the threshold until false matches appear. Electron, games, remote desktops, CAD canvases, and owner-drawn controls commonly require this route.

## Blank window capture

If the target is minimized, call `set_window_state` with `restore` first. Otherwise bring it forward with `activate_window` and capture again. The capture chain uses Windows Graphics Capture first, then Win32 `PrintWindow` and physical screen copy. Protected video, secure desktop, and some higher-integrity surfaces may remain unavailable.

## Input went to the wrong control

Stop coordinate retries. Inspect focus, use a stable semantic control id with `enter_text`, then wait for the expected UI state. All state-changing tools activate their selected window before input.

## UI lock busy

Wait for the other controller to finish. The broker intentionally uses the same lock file as the legacy pixel skill so two agents cannot interleave physical input. Expired locks are removed automatically.
