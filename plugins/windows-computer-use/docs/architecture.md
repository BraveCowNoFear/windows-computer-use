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

`inspect_window` resolves an exact Win32 HWND and walks UIA3 children breadth-first. Every descriptor includes parent id, depth, child count, semantic properties, patterns, physical bounds, and a stable selector. Public ids hash the HWND plus a hierarchy path; an AutomationId takes precedence over mutable accessible names. `observe_changes` compares a new observation with one of the 16 cached observation ids and returns only added, removed, or changed controls. `snapshot` returns a semantic observation together with one fresh image, screenshot id, capture time, and content hash.

The broker caches the live `AutomationElement`. If it becomes stale, the broker scans the current tree and re-locates by semantic properties. A major navigation should still be followed by a fresh inspection because the application may intentionally replace the entire view.

## Action pipeline

State-changing actions run as:

1. Resolve exactly one window.
2. Acquire the shared `codex-ui-control.lock.json` lock.
3. Restore and activate the target window.
4. Resolve the semantic control or physical point. Coordinate actions may bind to a screenshot id; semantic/input mutations invalidate older screenshots, and moved, resized, unknown, or expired observations are rejected.
5. Use UIA3 Pattern first, then SendInput fallback.
6. Wait a short settling interval or the explicit `wait_for_ui` condition.
7. Re-observe the control or window and return verification metadata. Pixel actions capture a post-action frame and return its id/hash for visual-difference inspection.
8. Release the shared lock.

The broker never logs raw text or screenshots. Its local JSONL audit stores timestamp, session, method, success, duration, and a SHA-256 hash of arguments.

## Native backends

- UIA3: FlaUI 5.0 over Microsoft UI Automation.
- Windows and capture: picker-free `Windows.Graphics.Capture` for HWNDs on Windows 10 1903+, then `PrintWindow(PW_RENDERFULLCONTENT)` and physical screen copy fallback.
- Displays and DPI: `EnumDisplayMonitors` + `GetMonitorInfo` physical virtual-screen rectangles and effective per-monitor DPI from Shcore.
- Input: User32 `SendInput`; Unicode uses `KEYEVENTF_UNICODE` and does not mutate the clipboard.
- OCR: Windows Runtime `Windows.Media.Ocr`, executed by a bundled PowerShell WinRT adapter and using installed language packs.
- Concurrency: atomic compatibility lock shared with `desktop-control-for-windows`.

## Known gaps

- There is no local visual-language model or template/image matcher yet; OCR plus model-side image reasoning is the visual fallback.
- Secure desktop and higher-integrity windows require matching Windows privileges.
- Deterministic, Explorer, Settings, Word, Excel, VS Code/Electron, WeChat, and SolidWorks gates are implemented. The current development machine has one 150%-scaled display, so true multi-monitor/mixed-DPI, remote-desktop, minimized/protected-window, and elevated-process runs remain external matrix items.
