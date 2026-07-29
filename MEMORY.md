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

## Current boundaries and next work

- No local visual-language model, image matcher, or OCR bounding-box grounding yet. Image interpretation currently depends on Windows OCR and the calling model.
- Broader release benchmarks remain for Office, WeChat, SolidWorks, Electron, remote desktop, multi-monitor, mixed-DPI, minimized/protected windows, and elevated-process boundaries.
- Browser DOM and app-specific APIs are routing guidance for the calling agent, not implemented inside this Windows broker.

## Tooling notes

- The current Codex CLI exposes marketplace `add`, `upgrade`, and `remove`, but `upgrade` rejects local-path marketplaces because it only upgrades Git-backed sources. Validate local changes through the manifest plus cold MCP launcher gate; do not treat that expected CLI rejection as a plugin failure.
