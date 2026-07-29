# Recovery guide

## Stale or missing semantic elements

Re-run `inspect_window`, then search by `automation_id`, exact `name`, and `control_type`. A cached `control_id` automatically attempts re-location from those properties. If duplicates remain, narrow the query or inspect a transient child window.

If the broker says a `window_id` itself is stale, Windows has destroyed that HWND and no unique same-process/class/title replacement was found. Re-run `list_windows` or `wait_for_window` and select the replacement. Temporary hiding, an empty title, and minimize/restore do not by themselves invalidate an exact HWND selector.

## Custom canvas or empty UIA tree

Use `find_text` when the target has readable text, `find_image` when you have a known local PNG/JPEG template, or snapshot the selected window for model-side vision. Use image-relative coordinates with `coordinate_space=screenshot` and pass the same `screenshot_id`. If matching fails after a DPI/zoom change, try the narrowest plausible bounded scale range; capture a current-scale template rather than lowering the threshold until false matches appear. Electron, games, remote desktops, CAD canvases, and owner-drawn controls commonly require this route.

## Blank window capture

If the target is minimized, call `set_window_state` with `restore` first. Otherwise bring it forward with `activate_window` and capture again. The capture chain uses Windows Graphics Capture first, then Win32 `PrintWindow` and physical screen copy. Protected video, secure desktop, and some higher-integrity surfaces may remain unavailable.

## Input went to the wrong control

Stop coordinate retries. Inspect focus, use a stable semantic control id with `enter_text`, then wait for the expected UI state. Window-scoped state-changing tools activate their selected window before input; virtual-desktop and direct-screen actions intentionally do not.

If a held key or mouse button outlives the intended gesture, call `key_up` or `mouse_up` with the same explicit name. `end_session` releases every tracked mouse button before every tracked key, and broker shutdown provides the same final cleanup backstop.

## Desktop screenshot rejected

Capture the virtual desktop again. Desktop screenshot ids are invalidated after input and rejected when they expire or when a monitor is connected, disconnected, repositioned, or changes resolution. Use `coordinate_space=screenshot` without a window selector for a bound desktop action. Because mouse-down invalidates its source image, finish a cross-action hold with explicit `coordinate_space=screen` coordinates.

## Clipboard write or restore failed

If `paste_text` or `write_clipboard_text` cannot materialize an existing direct format, it fails before mutation. Use direct `enter_text`/`type_text`, or explicitly choose `preserve_previous=false` only when replacing the user's clipboard is the intended result. `paste_text` restores after target-action/Value failure; if the restore itself fails, its error includes the still-valid backup id—retry `restore_clipboard` before `end_session`. For a manual write/paste sequence, always restore the saved id in cleanup.

## UI lock busy

Wait for the other controller to finish. The broker intentionally uses the same lock file as the legacy pixel skill so two agents cannot interleave physical input. Expired locks are removed automatically.
