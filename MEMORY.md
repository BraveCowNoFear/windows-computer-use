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

### v0.8.0 — rich UIA state and secondary actions

- Control descriptors now expose Value/read-only, selection, toggle, expand/collapse, and horizontal/vertical scroll state when supported. Window observations summarize document text, selected text, and selected control ids; `observe_changes` detects state-only transitions.
- Added `perform_secondary_action`, making 25 MCP tools. It provides explicit focus/raise, selection membership, toggle, expand/collapse, and semantic scroll actions and fails clearly if the required UIA pattern is unavailable.
- Exact numeric window ids are now rehydrated directly from their HWND even if a window is temporarily hidden or untitled. If an application recreates its top-level HWND, an old id follows only a unique replacement with the same process id, native class, and non-empty title; ambiguous matches fail closed.
- The deterministic E2E fixture adds an edit value, selectable text, a toggle, a selectable list, and explicit top-level HWND recreation. Repeated runs gate Value/read-only exposure, selected text, exact toggle-state diffs, selected-control summaries, constrained window recovery, all prior WGC/OCR/coordinate/dialog/minimize gates, and cleanup.

### v0.9.0 — semantic condition waits

- `wait_for_ui` now supports `value_equals`, `value_contains`, `selected`, `unselected`, `toggle_on`, `toggle_off`, `toggle_indeterminate`, `expanded`, `collapsed`, `readonly`, and `editable` in addition to existence/visibility/focus predicates. Value comparison is optionally case-sensitive.
- Wait polling re-resolves the HWND each time and never reports an observation completed after the requested deadline as a successful match. UIA calls remain synchronously non-preemptible.
- `find_controls`/`wait_for_ui` use selector-targeted traversal and enrich Pattern state only for matches. Exact control-id selectors reuse validated live-element locators, preserving stable ids while avoiding full rich-tree reads on every poll.
- Three consecutive E2E runs using stable control ids passed a Value equality wait in 0-16 ms, a real delayed Toggle transition in 234-235 ms, and a selected-control wait in 125-141 ms, all under a 1500 ms deadline, alongside the complete prior gate set.

### v0.10.0 — local non-text template grounding

- Added `find_image`, making 26 MCP tools. It fresh-captures one window and matches an exact-scale local PNG/JPEG using bounded coarse-to-fine sampled BGRA color distance, alpha weighting, top-candidate queues, and overlap suppression.
- Results contain score, screenshot-relative bounds/center, physical screen bounds, fresh screenshot id/time/hash, and `coordinate_space=screenshot`; they feed directly into the existing stale-checked pointer/click/scroll/drag path.
- Unit coverage builds a synthetic unique-color target and proves exact screenshot/screen coordinate recovery. Three consecutive real E2E runs crop a button from WGC, re-find it in a new WGC frame at score 1.0 in 78 ms without OCR, click through the returned screenshot id, and verify the changed UIA state.
- At this milestone matching was intentionally exact-scale rather than falsely claiming scale/rotation-invariant vision; v0.13 later added bounded multi-scale search. Novel image understanding still uses model-side vision; current local fallbacks are UIA -> OCR -> known template -> screenshot/model -> physical pixels.

### v0.11.0 — native keyboard state parity

- Added `key_down` and `key_up`, making 28 MCP tools. Explicit keys can remain held across later actions; held state is returned after each transition and every tracked key is released by `key_up`, `end_session`, or broker disposal.
- `press_key` now supports repeat/interval timing, normalizes modifiers before ordinary keys even when the input order is reversed, and honors the Shift/Ctrl/Alt bits from the active-layout `VkKeyScanW` result for printable characters.
- Named coverage now includes F13-F24, left/right Win/Shift/Ctrl/Alt, Print Screen, Pause/lock keys, numpad operations, browser navigation, media transport, and volume controls. Extended-key flags are set for navigation/right-modifier/media classes.
- Unit tests gate implied Shift, modifier ordering, held-modifier reuse, and extended/function-key planning. Three consecutive real E2E runs hold/release Shift and observe both events, prove repeated `A` produces `AA`, then leave Ctrl held and prove `end_session` releases exactly one key; a system-level async-key probe confirms Shift/Ctrl/Alt are all up afterward.

### v0.12.0 — persistent five-button mouse state

- Added `mouse_down` and `mouse_up`, making 30 MCP tools. Left/right/middle/X1/X2 buttons can remain held across later pointer, keyboard, and semantic actions; every transition returns tracked held-button state.
- `click` now covers all five buttons. `drag` accepts a configurable button, rejects ambiguous reuse of an already-held button, and always releases its self-contained hold through a `finally` path. `end_session` and broker disposal release mouse buttons before keys, while cleanup attempts every held input even after an individual failure.
- The deterministic WinForms fixture observes real MouseDown/MouseUp events. Fifteen unit cases cover tool/catalog and native button normalization; three consecutive full E2E runs prove left-button hold/move/release, right-button drag, handle recreation, and joint Ctrl+right-button session cleanup. A final system async-state probe confirms five mouse buttons plus Shift/Ctrl/Alt are all up and no project process remains.

### v0.13.0 — bounded multi-scale template grounding

- `find_image` remains exact 1.0x by default but now accepts `scale_min`, `scale_max`, and `scale_step`. The range is bounded to 0.25x-4x and at most 25 evaluated sizes; 1.0x is included when inside the requested range.
- Templates use high-quality resampling, the existing sampled BGRA/SAD coarse-to-fine search at each size, and global score ordering plus cross-scale overlap suppression. Each match returns its scale and scaled image/screen bounds while retaining the fresh screenshot id.
- Seventeen unit cases include an exact synthetic 1.5x recovery. Three consecutive real WGC E2E runs shrink a real button template to 80%, recover it at 1.25x in 406-438 ms, and successfully click through the returned screenshot coordinates; exact-scale matching remains score 1.0 in 109-125 ms.

### v0.14.0 — actionable full virtual-desktop observations

- Virtual-desktop `capture` and fresh desktop `ocr` observations are now cached as first-class screenshot ids. `find_text` and `find_image` accept `desktop=true`; desktop coordinates map from the physical virtual-screen origin and are rejected after age expiry or display-topology changes.
- `move_pointer`, `click`, `mouse_down`, `mouse_up`, `scroll`, and `drag` can consume a desktop screenshot without a window selector or foreground activation. Explicit `coordinate_space=screen` also supports direct no-selector input; state-changing desktop actions re-capture the desktop and return `desktop-screenshot-reobserve` plus a fresh id.
- Desktop/window selector conflicts fail before input. Eighteen unit cases gate the desktop visual schemas. Three consecutive full E2E runs on the current 2560x1600 150%-scaled desktop prove full-screen capture bounds, stale/conflict rejection, full-desktop OCR grounding of `SAVE`, screenshot-bound pointer/click/drag, direct-screen middle-button down/up, and clean joint input teardown.

### v0.15.0 — reversible native clipboard access

- Added `read_clipboard_text`, `write_clipboard_text`, and `restore_clipboard`, making 33 MCP tools. The Windows OLE clipboard backend runs every operation on a dedicated STA thread with native contention retries and shares the existing UI lock.
- Preserved writes materialize every direct clipboard format before mutation and fail closed when a value cannot be cloned safely. The backup token stays only in the owning Broker session; restore republishes all formats, waits for Windows delayed rendering, verifies the format set plus normalized text digest, then consumes the token.
- Nineteen unit tests gate the schemas and read-only annotations. Three consecutive real E2E runs write a random Unicode-safe marker, read it back, focus the semantic edit, prove system `Ctrl+V`, restore the pre-test Chromium text/HTML/custom-format clipboard without exposing its content, and confirm `end_session` has no orphaned backup token.

### v0.16.0 — atomic verified paste fallback

- Added `paste_text`, making 34 MCP tools. One Broker call now preserves all direct formats, focuses an exact semantic target, uses `Ctrl+A` replacement or `Ctrl+End` append plus real `Ctrl+V`, waits for exposed UIA Value state, and restores the original clipboard before returning.
- The temporary clipboard transaction restores through the same verified path when focus, input, re-observation, or Value verification fails. If restoration itself fails, the MCP error retains the session-local backup id so the caller can explicitly retry rather than losing recovery state.
- The WinForms fixture adds a deterministic read-only target. Repeated real E2E runs prove atomic replace, `Ctrl+End` append, and an intentional 150 ms Value timeout all restore the pre-test Chromium text/HTML/custom formats; raw clipboard tools and end-session orphan checks remain covered.

### v0.17.0 — atomic verified copy and stable recovery

- Added `copy_text`, making 35 MCP tools. It focuses an exact semantic target, preserves the current selection or sends `Ctrl+A`, seeds a unique clipboard marker, sends real `Ctrl+C`, waits for the native clipboard sequence number to change, returns Unicode text, and compares it with UIA selected text when available.
- Atomic copy reuses the full-format transaction and returns only after restoration. Restore now requires the original text/formats and `GetClipboardSequenceNumber` to remain unchanged for 150 ms; delayed clipboard publication causes the snapshot to be re-published instead of escaping after success.
- Three consecutive E2E runs cover select-all copy, current-selection copy, and intentional copy timeout from a non-text button, with the original Chromium text/HTML/custom formats verified after every path. Adding expected-error coverage exposed a harness deadlock: redirected MCP stderr was never read, so broker stack traces filled the pipe. E2E, real-app, and extended-app runners now drain stderr asynchronously.

### v0.18.0 — bounded broker recovery without blind replay

- MCP-to-Broker calls now have a configurable 180-second deadline (`WCU_BROKER_CALL_TIMEOUT_MS`; `0` disables it). A deadline, broken pipe, malformed payload, or response-id mismatch never retries the uncertain action; it replaces the Broker and requires a fresh observation.
- MCP tracks successful cross-call `key_down`/`mouse_down` state and conservatively includes an in-flight down/drag when recovery is uncertain. A fresh Broker receives an internal release request before later user actions; failed recovery remains pending rather than silently continuing.
- A deterministic fault-injection gate stages real Ctrl and left-button holds, forces a 250 ms `wait_for_window` transport deadline, proves the Broker PID changes while MCP stays alive, verifies both native input states are up, and successfully calls `list_windows` afterward. Twenty-five unit tests cover deadline defaults, override, disable, and invalid configuration alongside all prior contracts.
- Hard Broker termination invalidates its in-memory clipboard backup. Timeout/transport errors therefore explicitly require clipboard verification when the interrupted action used the clipboard; the plugin does not claim crash-atomic clipboard restoration.
- Full-gate repetition exposed an intermittent first Ctrl+C delivery miss. `copy_text` now divides its requested sequence-change window across two attempts and performs one semantic-refocus retry only after the first attempt proves no clipboard publication; unknown-state actions are still never replayed.
- Back-to-back E2E then exposed delayed clipboard replacement after an otherwise successful write. Text publication now requires the content and native sequence number to remain stable for 150 ms and re-publishes for up to two seconds, matching the restore path instead of relying on an immediate readback.

### v0.19.0 — unchanged-current-foreground keyboard parity

- `press_key`, `key_down`, `key_up`, and `type_text` now accept `desktop=true`, keeping the current foreground/focus and sending native input without resolving or activating another window. Explicit window mode remains the default; desktop mode rejects `window_id`, `title`, or `app` conflicts before input.
- Desktop key actions report the foreground HWND before/after, while cross-action held-key tracking and Broker-restart recovery remain shared with window mode. `key_up` can release globally even when Windows temporarily exposes no foreground HWND.
- Twenty-six unit tests gate all four desktop schemas. Real MCP E2E focuses the isolated edit once, then proves selector-free Ctrl+A, Unicode text, Shift down/up application events, foreground preservation, selector-conflict rejection, and final system input cleanup.

### v0.20.0 — atomic actionable desktop observation

- Added `observe_desktop`, making 36 MCP tools. One read-only locked call now returns a full virtual-desktop PNG with display topology, visible top-level windows, current physical pointer position, and a cached actionable screenshot id without activating any window.
- MCP emits the observation as compact structured text plus one image content block, omitting duplicate base64 from metadata. The desktop screenshot record reuses existing age/display-topology validation and can directly drive screenshot-space pointer, click, scroll, and drag operations.
- Twenty-seven unit tests gate tool uniqueness and read-only schema. Real E2E receives one 2560x1600 PNG, one 150%-scaled display, twelve visible windows including the isolated target, and a physical pointer descriptor, then uses that exact observation id for a verified screenshot-space pointer move.
- The Broker fault-injection harness now allows 25 seconds for an MCP response because production recovery intentionally has a 15-second restart/input-release budget. Its injected call deadline is 2000 ms against a deterministic 5000 ms native wait: a former 250 ms setting could incorrectly time out normal setup input under combined-test load, while a 5-second response reader could reject a valid slow recovery.

## Current boundaries and next work

- Windows OCR provides line/word grounding and bounded multi-scale local template matching covers known images. Novel non-text interpretation, rotation variation, and ambiguous scenes still depend on the calling model.
- Native Unicode typing remains the non-mutating default and `paste_text` is the atomic reversible fallback; callers using raw preserved writes must still restore their backup before `end_session` if the new clipboard content is not intentional.
- Remaining external matrix items are remote desktop, true multi-monitor/mixed-DPI hardware, protected windows, elevated-process boundaries, and longer state-changing workflows inside complex apps.
- Browser DOM and app-specific APIs are routing guidance for the calling agent, not implemented inside this Windows broker.

## Tooling notes

- The current Codex CLI exposes marketplace `add`, `upgrade`, and `remove`, but `upgrade` rejects local-path marketplaces because it only upgrades Git-backed sources. Validate local changes through the manifest plus cold MCP launcher gate; do not treat that expected CLI rejection as a plugin failure.
