# Architecture

## Design goals

1. Make semantic Windows automation the default, not coordinate guessing.
2. Keep control local: Codex speaks stdio MCP, the MCP owns a current-user native broker, and no screen data leaves the machine through this plugin.
3. Expose full current-user desktop control without a second plugin-owned approval system.
4. Re-observe after actions and recover stale elements before using physical pixels.
5. Coexist with the legacy Python fallback through its global UI lock file.

## Process model

The Codex plugin launches `WindowsComputerUse.Mcp.exe`. The MCP creates a random per-process named pipe and launches `WindowsComputerUse.Broker.exe`. The pipe uses `PipeOptions.CurrentUserOnly`; this is transport isolation, not an application allowlist. Each MCP instance owns and terminates its broker.

MCP stdout contains only newline-delimited JSON-RPC. Broker diagnostics and MCP diagnostics use stderr. Broker requests and responses are also newline-delimited JSON over the private named pipe.

## Observation and identity

`inspect_window` resolves an exact Win32 HWND and walks UIA3 children breadth-first. Every descriptor includes parent id, depth, child count, semantic properties, patterns, physical bounds, a stable selector, and supported Value/read-only/selection/toggle/expand/scroll state. Observations also summarize the focused element, selected controls, focused document text, and selected text. Public ids hash the HWND plus a hierarchy path; an AutomationId takes precedence over mutable accessible names. `observe_changes` compares a new observation with one of the 16 cached observation ids and returns only added, removed, or changed controls, including pure state transitions. `snapshot` returns a semantic observation together with one fresh image, screenshot id, capture time, and content hash. Window descriptors expose GetWindowRect and DWM visible-frame bounds, native class, immediate owner, owner-chain root, visibility, and minimized/maximized state. Exact HWND selectors are described directly rather than being rediscovered through the visible-window list, so temporarily hidden or untitled targets remain addressable. If an application destroys and recreates its top-level HWND, a cached id may follow the replacement only when process id, native class, and non-empty title identify exactly one candidate; ambiguous or unrelated replacements fail closed. `wait_for_window` polls native relationships for transient dialogs without fixed sleeps.

The broker caches the live `AutomationElement`. If it becomes stale, the broker scans the current tree and re-locates by semantic properties. A major navigation should still be followed by a fresh inspection because the application may intentionally replace the entire view.

`find_controls` and `wait_for_ui` use selector-targeted traversal rather than building a fully state-enriched tree on every poll. Non-matching elements read only identity, layout, focus, and availability; full Pattern state is read only for matched elements, and exact control-id selectors reuse a validated live-element cache. Waits re-resolve the window on every poll, enforce the requested deadline when deciding whether a late observation may match, and support Value, selection, toggle, expand/collapse, and read-only predicates.

## Action pipeline

State-changing actions run as:

1. Resolve exactly one window.
2. Acquire the shared `codex-ui-control.lock.json` lock.
3. Restore and activate the target window.
4. Resolve the semantic control or physical point. Coordinate actions may bind to a screenshot id; semantic/input mutations invalidate older screenshots, and moved, resized, unknown, or expired observations are rejected.
5. Use the requested UIA3 Pattern first. Primary `invoke` may fall back to a center click; `perform_secondary_action` stays semantic and fails explicitly when its required pattern is unavailable.
6. Wait a short settling interval or the explicit `wait_for_ui` condition.
7. Re-observe the control or window and return verification metadata. Pixel actions capture a post-action frame and return its id/hash for visual-difference inspection.
8. Release the shared lock.

The broker never logs raw text or screenshots. Its local JSONL audit stores timestamp, session, method, success, duration, and a SHA-256 hash of arguments.

## Native backends

- UIA3: FlaUI 5.0 over Microsoft UI Automation.
- Windows and capture: picker-free `Windows.Graphics.Capture` for HWNDs on Windows 10 1903+, then `PrintWindow(PW_RENDERFULLCONTENT)` and physical screen copy fallback. WGC image origins use `DWMWA_EXTENDED_FRAME_BOUNDS` rather than GetWindowRect's invisible resize border.
- Displays and DPI: `EnumDisplayMonitors` + `GetMonitorInfo` physical virtual-screen rectangles and effective per-monitor DPI from Shcore.
- Input: User32 `SendInput` plus verified `GetCursorPos`/`SetCursorPos`; Unicode uses `KEYEVENTF_UNICODE` and does not mutate the clipboard. Chords normalize modifiers before ordinary keys, infer the Shift/Ctrl/Alt bits returned by `VkKeyScanW`, send extended-key flags for navigation/right-modifier/media keys, and support repeat timing. `key_down`/`key_up` track held keys; `end_session` and broker disposal release them. Pointer moves support immediate or smooth screen/window/screenshot-space hover without activating a window.
- OCR: Windows Runtime `Windows.Media.Ocr`, executed by a bundled PowerShell WinRT adapter and using installed language packs. Lines/words carry image bounds; `find_text` returns screenshot and screen bounds plus image-relative centers.
- Image templates: local exact-scale PNG/JPEG matching over a fresh capture. A coarse-to-fine 12x12 sampled BGRA color-distance search, transparent-template weighting, bounded top-candidate queues, and overlap suppression return image/screen bounds plus a screenshot-bound center without uploading pixels.
- Concurrency: atomic compatibility lock shared with `desktop-control-for-windows`.
- Window lifecycle: direct HWND rehydration, verified Win32 minimize/maximize/restore, and owner-chain discovery. Visual tools reject minimized targets with explicit restore guidance instead of returning misleading frames.

## Known gaps

- There is no local visual-language model or scale/rotation-invariant feature matcher yet. Exact-scale templates, OCR, and model-side image reasoning form the current visual fallback stack.
- Secure desktop and higher-integrity windows require matching Windows privileges.
- Deterministic minimized/restore and owned-dialog transitions plus Explorer, Settings, Word, Excel, VS Code/Electron, WeChat, and SolidWorks gates are implemented. The current development machine has one 150%-scaled display, so true multi-monitor/mixed-DPI, remote-desktop, protected-window, and elevated-process runs remain external matrix items.
