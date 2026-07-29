# Recovery guide

## Stale or missing semantic elements

Re-run `inspect_window`, then search by `automation_id`, exact `name`, and `control_type`. A cached `control_id` automatically attempts re-location from those properties. If duplicates remain, narrow the query or inspect a transient child window.

## Custom canvas or empty UIA tree

Snapshot the selected window, run OCR if needed, and use window-relative coordinates from the fresh image. Pass its `screenshot_id` with the action. Electron, games, remote desktops, CAD canvases, and owner-drawn controls commonly require this route.

## Blank window capture

Bring the window forward with `activate_window` and capture again. The capture chain uses Windows Graphics Capture first, then Win32 `PrintWindow` and physical screen copy. Protected video, secure desktop, and some higher-integrity surfaces may remain unavailable.

## Input went to the wrong control

Stop coordinate retries. Inspect focus, use a stable semantic control id with `enter_text`, then wait for the expected UI state. All state-changing tools activate their selected window before input.

## UI lock busy

Wait for the other controller to finish. The broker intentionally uses the same lock file as the legacy pixel skill so two agents cannot interleave physical input. Expired locks are removed automatically.
