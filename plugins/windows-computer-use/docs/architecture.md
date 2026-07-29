# Architecture

## Design goals

1. Make semantic Windows automation the default, not coordinate guessing.
2. Keep control local: Codex speaks stdio MCP, the MCP owns a current-user native broker, and no screen data leaves the machine through this plugin.
3. Expose full current-user desktop control without a second plugin-owned approval system.
4. Re-observe after actions and recover stale elements before using physical pixels.
5. Coexist with the legacy Python fallback through its global UI lock file.

## Process model

The Codex plugin launches `WindowsComputerUse.Mcp.exe`. The MCP creates a random per-process named pipe and launches `WindowsComputerUse.Broker.exe`. The pipe uses `PipeOptions.CurrentUserOnly`; this is transport isolation, not an application allowlist. Each MCP instance owns and terminates its broker. No dedicated UI worker or sub-agent is required.

MCP stdout contains only newline-delimited JSON-RPC. Broker diagnostics and MCP diagnostics use stderr. Broker requests and responses are also newline-delimited JSON over the private named pipe.

Each broker call has a configurable MCP-side transport deadline (`WCU_BROKER_CALL_TIMEOUT_MS`, default 180000 ms, `0` disables it). UI Automation providers are synchronous inside the native process, so an expired call is not retried: the MCP kills the unresponsive broker, starts a fresh one, releases every plugin-tracked held key/button through an internal recovery request, and reports that the interrupted UI state is unknown. A broken pipe or malformed response follows the same reset path. The next action must begin with a fresh observation. If restart or input release itself fails, that state remains pending and is retried before any later user action. An interrupted clipboard transaction may have changed the system clipboard after its in-memory backup became unavailable, so the error also requires clipboard verification for clipboard tools.

## Observation and identity

`inspect_window` resolves an exact Win32 HWND and walks UIA3 children breadth-first. Every descriptor includes parent id, depth, child count, semantic properties, patterns, physical bounds, a stable selector, and supported Value/read-only/selection/toggle/expand/scroll state. Observations also summarize the focused element, selected controls, focused document text, and selected text. Public ids hash the HWND plus a hierarchy path; an AutomationId takes precedence over mutable accessible names. `observe_changes` compares a new observation with one of the 16 cached observation ids and returns only added, removed, or changed controls, including pure state transitions. `snapshot` returns a semantic observation together with one fresh image, screenshot id, capture time, and content hash. Window descriptors expose GetWindowRect and DWM visible-frame bounds, native class, immediate owner, owner-chain root, visibility, and minimized/maximized state. Exact HWND selectors are described directly rather than being rediscovered through the visible-window list, so temporarily hidden or untitled targets remain addressable. If an application destroys and recreates its top-level HWND, a cached id may follow the replacement only when process id, native class, and non-empty title identify exactly one candidate; ambiguous or unrelated replacements fail closed. `wait_for_window` polls native relationships for transient dialogs without fixed sleeps.

The broker caches the live `AutomationElement`. If it becomes stale, the broker scans the current tree and re-locates by semantic properties. A major navigation should still be followed by a fresh inspection because the application may intentionally replace the entire view.

`find_controls` and `wait_for_ui` use selector-targeted traversal rather than building a fully state-enriched tree on every poll. Non-matching elements read only identity, layout, focus, and availability; full Pattern state is read only for matched elements, and exact control-id selectors reuse a validated live-element cache. Waits re-resolve the window on every poll, enforce the requested deadline when deciding whether a late observation may match, and support Value, selection, toggle, expand/collapse, and read-only predicates.

`observe_desktop` acquires the same read-side UI lock once and returns one virtual-screen capture together with the current display topology, visible top-level window descriptors, and physical pointer position. The capture is cached as a normal desktop screenshot record, so its id enters the existing age/topology validation path for screenshot-space input without an extra observation call. It does not activate a window.

`wait_for_visual_change` accepts one cached screenshot id and repeatedly captures its exact original window or virtual desktop. It rejects an expired source, recreated/moved/resized window, or changed display topology before comparing exact PNG SHA-256 content. It never activates a source and returns the last capture as a newly cached actionable screenshot on either match or timeout. Exact image change is intentionally broad, so semantic `wait_for_ui` remains preferred when the desired condition is exposed; animations, carets, clocks, and unrelated desktop pixels are valid visual changes.

`wait_for_visual_stable` shares that identity/freshness path but establishes a new candidate hash from the first capture after the call begins. Every changed sample resets its stability clock; success requires continuously identical exact PNGs for `stable_ms` before the overall deadline. Both stable and timeout results cache and return the final image. This prevents acting midway through deterministic animation/loading, while dynamic full-frame content may intentionally remain unstable.

`window_from_point` provides the inverse bridge from a physical screen pixel to native identity. Win32 `WindowFromPoint` resolves the actual child HWND at that pixel; `GetAncestor(GA_ROOT)` maps it to a normal `WindowDescriptor` that can feed UIA inspection. The result retains child handle/class/title for owner-drawn or nested native surfaces and performs no pointer movement, activation, or input.

## Action pipeline

State-changing actions run as:

1. Resolve exactly one window.
2. Acquire the shared `codex-ui-control.lock.json` lock.
3. Restore and activate the target window.
4. Resolve the semantic control or physical point. Coordinate actions may bind to a screenshot id; semantic/input mutations invalidate older screenshots, and moved, resized, unknown, or expired observations are rejected.
5. Use the requested UIA3 Pattern first. Primary `invoke` may fall back to a center click; `perform_secondary_action` stays semantic and fails explicitly when its required pattern is unavailable.
6. Wait for an explicit `wait_for_ui`, `wait_for_window`, pixel-change, or continuous visual-stability condition.
7. Re-observe the control or window and return verification metadata. Pixel actions capture a post-action frame and return its id/hash for visual-difference inspection.
8. Release the shared lock.

The broker never logs raw text or screenshots. Its local JSONL audit stores timestamp, session, method, success, duration, and a SHA-256 hash of arguments.

## Native backends

- UIA3: FlaUI 5.0 over Microsoft UI Automation.
- Windows and capture: picker-free `Windows.Graphics.Capture` for HWNDs on Windows 10 1903+, then `PrintWindow(PW_RENDERFULLCONTENT)` and physical screen copy fallback. WGC image origins use `DWMWA_EXTENDED_FRAME_BOUNDS` rather than GetWindowRect's invisible resize border. Full virtual-desktop copies are cached as first-class screenshot observations; coordinates are validated against current monitor topology and can drive input without foregrounding a window.
- Displays and DPI: `EnumDisplayMonitors` + `GetMonitorInfo` physical virtual-screen rectangles and effective per-monitor DPI from Shcore.
- Input: User32 `SendInput` plus verified `GetCursorPos`/`SetCursorPos`; Unicode uses `KEYEVENTF_UNICODE` and does not mutate the clipboard. Chords normalize modifiers before ordinary keys, infer the Shift/Ctrl/Alt bits returned by `VkKeyScanW`, send extended-key flags for navigation/right-modifier/media keys, and support repeat timing. Keyboard tools normally resolve and activate one exact window; `desktop=true` instead snapshots the current foreground HWND and sends without activation, and conflicts with any window selector fail before input. `key_down`/`key_up` track held keys across either mode. `mouse_down`/`mouse_up` track left/right/middle/X1/X2 holds across moves and other actions, while self-contained drag accepts the same five buttons and releases in a `finally` path. `end_session` and broker disposal release mouse buttons before keys. Pointer moves support immediate or smooth screen/window/screenshot-space hover without activating a window.
- OCR: Windows Runtime `Windows.Media.Ocr`, executed by a bundled PowerShell WinRT adapter and using installed language packs. Lines/words carry image bounds; `find_text` returns screenshot and screen bounds plus image-relative centers.
- Image templates: local PNG/JPEG matching over a fresh capture, exact 1.0x by default or across an explicit bounded 0.25x-4x range of at most 25 sizes. High-quality resampling, coarse-to-fine 12x12 sampled BGRA color-distance search, transparent-template weighting, bounded top-candidate queues, and cross-scale overlap suppression return the matched scale, image/screen bounds, and screenshot-bound center without uploading pixels.
- Clipboard: Windows OLE clipboard access runs on dedicated STA threads with contention retries. A preserved write first materializes every direct format into an in-process snapshot, then publishes Unicode text and requires its content plus clipboard sequence to remain stable for 150 ms, retrying bounded external ownership contention for up to ten seconds. Restore uses the same rule for the original format set and normalized text digest before consuming the session token. `paste_text` holds that transaction through focus, replace/append Ctrl+V, and Value verification. `copy_text` seeds a unique marker, sends current/all-selection Ctrl+C, waits on `GetClipboardSequenceNumber`, and performs at most one semantic-refocus retry when publication is missing or the copied text disagrees with an observable UIA selection; it then captures Unicode text and restores. All success/failure paths use the same restoration code and audit logs never contain clipboard contents.
- Concurrency and recovery: atomic compatibility lock shared with `desktop-control-for-windows`; bounded broker transport with no blind action replay and conservative held-input release after process replacement.
- Window lifecycle: direct HWND rehydration, verified Win32 minimize/maximize/restore, exact physical-pixel `SetWindowPos` move/resize with optional activation, and owner-chain discovery. Geometry restores minimized/maximized windows first, preserves foreground by default, supports negative virtual-screen origins, and requires exact `GetWindowRect` readback. Visual tools reject minimized targets with explicit restore guidance instead of returning misleading frames.

## Known gaps

- There is no local visual-language model or rotation-invariant feature matcher yet. Bounded multi-scale templates, OCR, and model-side image reasoning form the current visual fallback stack.
- Secure desktop and higher-integrity windows require matching Windows privileges.
- Deterministic minimized/restore and owned-dialog transitions plus Explorer, Settings, Word, Excel, VS Code/Electron, WeChat, and SolidWorks gates are implemented. The current development machine has one 150%-scaled display, so true multi-monitor/mixed-DPI, remote-desktop, protected-window, and elevated-process runs remain external matrix items.
