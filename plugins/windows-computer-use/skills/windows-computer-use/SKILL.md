---
name: windows-computer-use
description: Control Windows desktop applications through the local Windows Computer Use MCP with UI Automation 3, stable semantic control ids, native Unicode keyboard/mouse input, window capture, Windows OCR, condition waits, and automatic post-action verification. Use for operating Windows apps, dialogs, settings, File Explorer, Office, WeChat, Electron, CAD, or any visible desktop UI when an app API or browser DOM is unavailable or insufficient.
---

# Windows Computer Use

Use the local `windowsComputerUse` MCP to operate Windows directly. It runs in `full-control` mode: it imposes no plugin-owned app allowlist or action confirmation layer. Codex host permissions and Windows integrity boundaries still apply.

## Control stack

Choose the highest reliable layer for each step:

1. Use an application API or browser DOM when already available.
2. Use `inspect_window`, `find_controls`, `invoke`, and `enter_text` for UIA3 semantic control.
3. Use `wait_for_ui` after transitions instead of fixed sleeps.
4. Use `observe_changes` with the previous `observation_id` when only incremental UIA state is needed.
5. Use `snapshot` for one atomic UIA + image observation; use `capture` plus `ocr` when semantic metadata is missing or incomplete.
6. Use `click`, `press_key`, `type_text`, `scroll`, or `drag` for physical input fallback.

Do not delegate to a dedicated UI worker. Drive this MCP directly in the active task.

## Workflow

1. Call `list_windows`. Select exactly one returned window and keep its numeric `window_id`; never invent a window object or guess a handle.
2. Call `inspect_window` or `find_controls`. Prefer a returned stable `control_id` over coordinates.
3. Perform one state-changing action. Actions automatically activate the target, re-resolve stale elements, and re-observe afterward.
4. Inspect the returned verification. Call `wait_for_ui` for dialogs, navigation, saves, progress, or any asynchronous transition.
5. Reinspect after a major state change. Treat prior coordinates as stale; control ids can be retried because the broker re-locates their selector.
6. When UIA cannot expose the target, call `snapshot` (and optionally `ocr`) before coordinate input, then pass its `screenshot_id` to `click`, `scroll`, or `drag`. If the broker reports it stale, moved, or resized, snapshot again instead of retrying blindly.
7. Call `end_session` after the Windows phase to clear cached elements and close the control lifecycle.

All coordinates are physical screen pixels. Coordinate tools are window-relative by default and support negative virtual-desktop origins. Unicode text entry does not require clipboard mutation.

The broker shares `codex-ui-control.lock.json` with `desktop-control-for-windows`, so native and legacy fallback actions serialize without extra confirmation prompts.

Read [references/tool-reference.md](references/tool-reference.md) for selectors and exact tool behavior. Read [references/recovery.md](references/recovery.md) when UIA elements disappear, capture is blank, or an app uses a custom canvas.
