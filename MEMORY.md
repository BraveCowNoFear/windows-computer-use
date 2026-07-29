# Project memory

This file is the compact, repo-local memory for Windows Computer Use. `AGENTS.md` defines the binding rules; this file records durable decisions, verified milestones, and honest remaining gaps.

## Product decisions

- Deliver one Codex marketplace plugin composed of a skill, local stdio MCP, and C#/.NET native Windows broker.
- Drive the MCP directly from the active task. Do not require a dedicated UI worker or sub-agent.
- Default to full current-user desktop control without a plugin-owned app allowlist, action blocklist, or confirmation matrix. Codex host policy, Windows integrity levels, UAC secure desktop, and OS policy remain external boundaries.
- Reliability order is application API/browser DOM, UIA3 semantics, Windows OCR/model image reasoning, then physical SendInput pixels.
- Use exact HWND-derived window ids, stable semantic control ids, condition waits, and post-action re-observation. Coordinate actions should bind to a fresh screenshot id.
- Keep compatibility with the shared `codex-ui-control.lock.json` protocol used by `desktop-control-for-windows`.
- Keep MCP stdout strictly newline-delimited JSON-RPC. Raw entered text and image data must not be written to audit logs.

## Verified milestones

### v0.1.0 — semantic native-control baseline

- Implemented UIA3 inspection/search, stable selector re-location, semantic invoke/text entry, Win32 window activation, SendInput mouse/keyboard, Windows.Media.Ocr, capture fallback, current-user named pipe, shared UI lock, and 16 MCP tools.
- Verified deterministic WinForms E2E plus non-destructive Explorer and Settings compatibility. Published the public GitHub repository and passed Windows CI.

### v0.2.0 — native capture and observation freshness

- Added picker-free `Windows.Graphics.Capture` for HWNDs using a D3D11 free-threaded frame pool. Backend order is WGC, `PrintWindow`, then screen copy.
- Added `snapshot`, making 17 MCP tools. It returns UIA state and image content from one locked observation with screenshot id, UTC capture time, and SHA-256.
- Added screenshot-bound `click`, `scroll`, and `drag`. Unknown, cross-window, moved/resized, or expired observations are rejected before SendInput. Pixel actions capture a post-action frame and return verification hash/id.
- E2E hard-gates WGC, an occluded target window, snapshot metadata, stale screenshot rejection, Unicode UIA text, semantic invoke, wait, OCR, MCP cold launch, cleanup, and zero leftover screenshots/UI locks.
- Real-app smoke verified WGC + UIA + OCR on File Explorer (124 controls) and Settings (157 controls) while preserving the user's existing Notepad window.

### v0.3.0 — hierarchical and incremental semantics

- UIA inspection now walks a real hierarchy and returns `parentId`, `depth`, and `childCount`. Stable ids hash the HWND and semantic hierarchy path; AutomationId-backed controls do not churn when their accessible name changes.
- Added `observe_changes`, making 18 MCP tools. The broker retains 16 semantic observations and returns only added, removed, or changed descriptors relative to an earlier `observation_id`.
- Semantic invoke/text and physical input invalidate older screenshot ids. E2E proves a changed status label produces one incremental diff and that coordinates tied to the earlier screenshot are rejected.
- Hierarchical real-app regression passed Explorer (124 controls) and Settings (157-170 controls depending on load timing) with WGC and OCR.
- The real-app gate now requires WGC and treats empty/failed OCR as failure after one fresh-capture retry; an earlier benchmark incorrectly reported overall success when Settings OCR transiently returned `ok=false`.

### v0.4.0 — display topology and extended compatibility

- Added `display_info`, making 19 MCP tools. It returns physical virtual-desktop bounds and each display's bounds, work area, primary flag, effective DPI, and scale percentage.
- The development machine currently exposes one 2560x1600 physical display at 150% scaling. The deterministic E2E hard-gates at least one display and valid DPI metadata; no multi-monitor claim is made from this machine.
- Added an isolated extended-app gate. Verified Word (138 controls, depth 11), Excel (60, depth 8), VS Code/Electron (14, depth 7), WeChat (29, depth 8), and SolidWorks (115, depth 12), all with WGC + OCR.
- Extended-app cleanup preserves pre-existing app families, closes only test-created process groups, and proved zero remaining target processes, screenshots, VS Code profiles, MCP/Broker processes, or UI locks.

### v0.5.0 — screenshot coordinates and OCR grounding

- Window descriptors now expose both GetWindowRect and DWM extended visible-frame bounds. WGC capture bounds use the visible-frame origin, eliminating invisible resize-border offset.
- `click`, `scroll`, and `drag` support explicit `window`, `screen`, and `screenshot` coordinate spaces. Screenshot points require the matching fresh screenshot id and are range-checked before SendInput.
- Fresh window OCR now returns screenshot id, capture bounds, timestamp, hash, and `coordinate_space=screenshot`. Added `find_text`, making 20 MCP tools; it returns matching OCR line/word image bounds, physical screen bounds, and image-relative centers.
- E2E enters a new value, locates the large `SAVE` button through OCR, clicks its OCR word center in screenshot space, and verifies the resulting UIA status. This proves the full WGC -> OCR -> coordinate mapping -> SendInput -> UIA verification loop.

### v0.6.0 — transient windows and native state

- Window descriptors now include native class, immediate owner, owner-chain root, and maximized state. `list_windows` can optionally include titleless top-level surfaces.
- Added `wait_for_window` selectors for title/app/class/process/owner/root-owner and `exists`/`absent` conditions. Added verified `set_window_state` for minimize/maximize/restore, making 22 MCP tools.
- Visual tools explicitly reject minimized targets and instruct the caller to restore first; they never label an unreliable minimized frame as a successful observation.
- E2E opens a real owned WinForms dialog asynchronously, proves its owner/root-owner linkage, closes it semantically, waits for absence, minimizes the main window, proves capture rejection, and restores it.

### v0.7.0 — pointer parity

- Added `pointer_position` and `move_pointer`, making 24 MCP tools. Pointer movement can be immediate or smoothly interpolated for hover interactions and deliberately does not click or activate a window.
- Screen coordinates work without a window selector. Window coordinates use the exact HWND bounds; screenshot coordinates can resolve their source window directly from a fresh screenshot id and still run stale/moved/resized validation.
- E2E verifies the final physical pointer position in all three coordinate spaces, including smooth movement to an OCR-grounded word center.
- `validate-plugin.ps1` gates manifest resources, marketplace resolution, MCP launcher wiring, skill identity, and PowerShell syntax locally and in GitHub Actions; `test.ps1` runs it before compilation.

## Current boundaries and next work

- Windows OCR now provides line/word grounding, but there is no local visual-language model or image matcher yet. Non-text image interpretation still depends on the calling model.
- Remaining external matrix items are remote desktop, true multi-monitor/mixed-DPI hardware, protected windows, elevated-process boundaries, and longer state-changing workflows inside complex apps.
- Browser DOM and app-specific APIs are routing guidance for the calling agent, not implemented inside this Windows broker.

## Tooling notes

- The current Codex CLI exposes marketplace `add`, `upgrade`, and `remove`, but `upgrade` rejects local-path marketplaces because it only upgrades Git-backed sources. Validate local changes through the manifest plus cold MCP launcher gate; do not treat that expected CLI rejection as a plugin failure.
