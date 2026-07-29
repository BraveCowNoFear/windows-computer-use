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

### v0.21.0 — verified physical window geometry

- Added `set_window_bounds`, making 37 MCP tools. It targets one exact HWND and uses Win32 `SetWindowPos` with physical virtual-desktop `x/y/width/height`; negative origins are accepted and positive dimensions are required.
- Minimized/maximized targets restore before movement. Foreground is preserved by default through `SWP_NOACTIVATE`, while `activate=true` uses the existing verified activation path. Completion requires exact `GetWindowRect` equality and returns the before/after rectangles in action verification.
- Twenty-eight unit tests gate required geometry fields. Three consecutive real E2E runs move the isolated WinForms window by 20 physical pixels, enlarge it by 40x30, prove foreground state is unchanged, and restore the exact original rectangle before running every prior semantic/visual/input/clipboard gate. True multi-monitor placement is not claimed from the one-monitor development machine.

### v0.22.0 — native point-to-window identity bridge

- Added read-only `window_from_point`, making 38 MCP tools. Physical virtual-desktop x/y resolves through Win32 `WindowFromPoint`; `GetAncestor(GA_ROOT)` returns a normal top-level `WindowDescriptor`, while the actual child HWND/class/optional title remains available for nested native surfaces.
- The hit test acquires the shared UI lock but never moves the pointer, activates a target, or sends input. It lets a desktop/screenshot-derived point be checked against a stable window id before choosing UIA or screenshot-bound mouse action.
- Twenty-nine unit tests gate required coordinates and read-only annotations. Three consecutive real E2E runs activate only the isolated fixture, hit-test its visible client center, observe a concrete child HWND, prove the root id equals the fixture's stable id, and then complete every previous gate.
- A later combined gate showed the existing mouse-event UIA observation could exceed its former 1500 ms harness budget under load even after `mouse_down` had verified held native state. Only the deterministic down/up observation budget was raised to 3000 ms; input behavior and product defaults did not change.

### v0.23.0 — exact visual-change condition wait

- Added read-only `wait_for_visual_change`, making 39 MCP tools. A fresh cached screenshot id identifies its own original window or virtual desktop; callers do not repeat or guess a selector.
- The Broker validates source age at entry and window identity/bounds or virtual-desktop topology on every poll. It compares exact PNG SHA-256 content, never activates the source, rejects a late match after the requested deadline, and returns the final capture as a new actionable screenshot on both match and timeout.
- Exact image change is deliberately broad: animations, blinking carets, clocks, and unrelated desktop pixels can satisfy it. The skill and references therefore retain `wait_for_ui`/`wait_for_window` as the preferred conditions whenever semantic state exists.
- Thirty unit tests gate the required screenshot contract and read-only annotation. Real WGC E2E drives a two-phase delayed WinForms toggle, first proves the UIA `toggle_on` transition, then captures a baseline and waits for the second pixel transition; the returned PNG has a new id/hash bound to the baseline and UIA independently verifies the final `toggle_off` state.

### v0.24.0 — continuous exact-frame stability wait

- Added read-only `wait_for_visual_stable`, making 40 MCP tools. It reuses a fresh screenshot's original window/desktop identity, establishes stability only from the first new capture after invocation, resets on every changed PNG SHA-256, and returns the latest actionable image on success or timeout.
- Window identity/bounds, desktop topology, source age, cancellation, and late-result deadline semantics are shared with `wait_for_visual_change`. Exact full-frame stability is deliberately strict and can remain false for clocks, carets, video, notifications, or other dynamic pixels.
- Thirty-one unit tests gate the required stability interval and read-only schema. Real WGC E2E drives five 250 ms rendering frames, first proves a 500 ms deadline returns `stable=false` with an actionable image, then chains that image into a successful wait requiring at least 500 ms of continuous unchanged samples and independently reads the restored semantic heading.
- Repeated full-gate work exposed two older clipboard races before the new visual stage: an external sequence change could publish text that disagreed with the UIA selection, and transient ownership could outlast one restore attempt. `copy_text` now spends its single semantic-refocus retry on either missing publication or a provable UIA mismatch; publish/restore loops tolerate bounded external ownership contention for up to ten seconds while retaining verified format/text restoration.

### v0.25.0 — actionable region capture and region-scoped waits

- Added read-only `capture_region`, making 41 MCP tools. Required `x/y/width/height` crop a fresh window or desktop frame in source-image coordinates and return a compact PNG whose bounds and screenshot-space coordinates map directly to the cropped physical origin.
- Screenshot cache identity now separates full source bounds from public cropped bounds and retains the relative image region. Window geometry or desktop topology is still validated against the full source; `wait_for_visual_change` and `wait_for_visual_stable` automatically re-capture/crop only the same region.
- The first chained stability E2E exposed that wait results initially lost private crop identity even though their PNG/bounds were correct. Wait results now inherit source bounds and region, so a timeout image can feed the next wait or screenshot-bound action without being mistaken for a full frame.
- Thirty-two unit tests gate rectangle requirements/read-only schema. Real WGC E2E captures and physically maps a 64x64 desktop region, detects the delayed Toggle transition in a 172x40 window region, chains timeout-to-stable waits over a 340x60 heading region, and completes all prior semantic, input, clipboard, OCR, image, lifecycle, and cleanup gates.

### v0.26.0 — exact cached and nested screenshot crops

- `capture_region` can now take a fresh `screenshot_id` and crop the exact cached PNG bytes rather than acquiring a second frame after observation. The screenshot is authoritative: mixing it with `desktop=true` or a window selector fails before capture; age plus original window geometry or desktop topology are revalidated.
- Screenshot records retain their compressed capture under a 32-entry and 32 MiB base64-character budget while always preserving the newest record. A crop keeps full-source identity, and nested crop offsets are combined relative to that original full image so later screenshot-space input and region-scoped waits remain correct.
- Thirty-two unit tests gate the new optional `screenshot_id`/`max_age_ms` schema. Real WGC E2E crops the existing atomic desktop observation to 64x64, crops that result again to 32x32, proves physical pointer mapping, rejects a conflicting window selector, derives both visual-change and visual-stability regions from their exact observed window frames, and completes the full prior gate.
- The first combined gate exposed one old OCR helper run that exited successfully with invalid JSON despite the preceding standalone E2E passing. The helper and broker stream reader now agree on BOM-free UTF-8, and only this read-only saved-image recognition is retried once when a successful helper exit still produces malformed JSON; UI input is never replayed.

### v0.27.0 — exact cached screenshot OCR and text grounding

- `ocr` and `find_text` now accept a fresh `screenshot_id` for full, cropped, or nested-cropped images. They validate age plus original window geometry/desktop topology and recognize the cached PNG bytes without acquiring a second frame; selectors are rejected because the screenshot is authoritative. `ocr path` is likewise explicit and mutually exclusive with cached/fresh selectors.
- Exact recognition materializes only a temporary local PNG for the Windows Runtime adapter and deletes it on success/failure. The returned OCR metadata retains the original screenshot id/hash/bounds, while `find_text` line/word centers and physical bounds remain directly actionable against the exact recognized pixels.
- Thirty-three unit tests gate `screenshot_id` and `max_age_ms` for both tools. Real WGC E2E derives a padded button region from an atomic snapshot, proves exact region OCR and exact `find_text` share its id/hash/bounds, rejects a conflicting selector, clicks through the region-relative center, then uses exact cached full-desktop OCR to complete the whole-screen action path.

### v0.28.0 — exact cached screenshot template matching

- `find_image` now accepts a fresh `screenshot_id` for full, cropped, or nested-cropped PNGs. It reuses the common age and original window-geometry/desktop-topology validation, rejects mixed selectors, and passes the cached `CaptureResult` directly to the in-process matcher without a second capture or temporary source file.
- Exact and bounded multi-scale matches retain the original screenshot id/hash/cropped physical bounds plus image/screen match bounds and center, so a result remains directly actionable against precisely the model-observed frame.
- Thirty-three unit tests gate the added schema. Real WGC E2E extracts a button template and padded search region from one saved window frame, rejects a conflicting selector, proves exact and 1.15-1.35 multi-scale searches return the region id/hash/bounds, clicks through the same region id, and completes all prior gates.

### v0.29.0 — localized exact screenshot differences

- Added read-only `compare_screenshots`, making 42 MCP tools. Both fresh cached ids must share window/desktop identity, geometry/topology, full-source bounds, capture bounds, and private crop identity before decoded 32-bit BGRA pixels are compared; the tool never captures, activates, or sends input.
- Results include exact changed-pixel count/fraction, maximum channel delta, exact image/physical-screen union bounds, and four-neighbor connected tile regions. `channel_threshold` defaults to exact zero; tile size and maximum output regions are bounded, and omitted region count is explicit.
- Thirty-seven unit tests cover protocol, identical frames, exact disjoint pixel counts/bounds, negative physical origins, region grouping, and inclusive channel-threshold boundaries. Real WGC E2E compares the delayed Toggle baseline/result 172x40 crops, localizes 396 changed pixels into one region on the development run, verifies physical-bound translation, and completes every prior gate.

### v0.30.0 — inline visual verification for screenshot-bound actions

- Screenshot-bound `click`, `scroll`, and self-contained `drag` now add `data.visual_diff` after their existing post-action capture. Full/desktop baselines compare directly; cropped/nested baselines automatically crop the post-action full frame back to the same private region before exact BGRA comparison.
- Inline summaries include comparable/changed state, changed pixels/fraction, maximum channel delta, exact image/physical union bounds, and up to 20 localized regions. If source bounds changed, the completed input remains successful and returns `comparable=false` with geometry evidence instead of throwing; unbound/direct-screen actions have no trusted baseline.
- Thirty-seven unit tests remain green. Real WGC E2E verifies region-bound window clicks, a full-desktop Save click with 2854 changed pixels on the development run, a no-op-capable desktop wheel action, desktop middle-button drag, and template-region click all return comparable summaries before completing every prior gate.

### v0.31.0 — continuous screenshot-bound mouse gestures

- `mouse_down` and `mouse_up` now re-observe after every input while preserving their higher-priority held-button verification. Each returns `after_screenshot_id`; screenshot-bound window/desktop calls also return the same exact `data.visual_diff` evidence as click/scroll/drag.
- A cross-call gesture can chain the post-down screenshot id into `mouse_up`, eliminating the prior continuity gap where the initiating action invalidated its only grounded screenshot. Unbound window and direct-screen calls still provide a fresh post-action id but correctly omit diff evidence without a trusted baseline.
- Thirty-seven unit tests remain green. Real WGC E2E chains screenshot-bound left-button down/up against a real window and middle-button down/up against the full desktop, requires comparable changed pixels at both transitions, verifies clean held state, and completes all 42-tool prior gates.

### v0.32.0 — verified pointer movement and hover continuity

- Every `move_pointer` now invalidates older screenshot ids, re-observes its window or virtual-desktop source, and returns a fresh full-source `after_screenshot_id`. Screenshot-bound full/region/nested moves also return exact top-level `visual_diff`; unbound/direct moves correctly return null diff with their fresh frame.
- Long gestures now chain the newest screenshot through `mouse_down` → `move_pointer` → `mouse_up`. Real WGC E2E verifies WinForms MouseEnter/MouseLeave pixel changes, all three coordinate spaces, full desktop plus region/nested-region mapping, old-id rejection, and both window/desktop held-button chains while completing every prior 42-tool gate.
- Extra capture pressure exposed two pre-existing E2E environment races: local WinForms timers could be collected before their final Tick, and an unrelated open IME candidate could intercept Ctrl+A. The test form now roots active timers through completion and disposes them deterministically; the input selection setup dismisses only transient composition with Escape without changing the user's input method.

### v0.33.0 — keyboard visual verification

- `press_key`, `key_down`, `key_up`, and `type_text` now capture before/after every input and return a fresh `data.after_screenshot_id`, exact changed state, and localized `data.visual_diff` while retaining foreground or held-key state as the primary verification.
- Window mode compares the selected window; `desktop=true` preserves the current foreground and compares the entire virtual desktop so system-level changes remain visible. Desktop `key_up` keeps its special no-foreground release guarantee to prevent stranded input.
- The E2E harness now has a `KeyboardVisual` scenario that avoids unrelated prior gates. Its focused 7.7-second real WGC/MCP run proves window and desktop shortcut, Unicode typing, key-down, and key-up visual evidence across all six new paths, with 42-tool protocol identity and clean held state.

### v0.34.0 — 语义动作视觉验证

- `invoke`、`perform_secondary_action` 与 `enter_text` 在保留 UIA 控件重观察主验证的同时，自动抓取动作前后窗口，返回新的 `data.after_screenshot_id`、变化状态与精确 `data.visual_diff`。
- 已完成动作若关闭来源窗口，不会被误报为失败；Broker 改为返回新的虚拟桌面截图，并以 `source-window-unavailable` 明确标记不可比较，供调用方从真实动作后状态继续。
- 新增独立 `SemanticVisual` 场景，没有重复旧端到端套件。Release 构建与定向真实 WGC/MCP 运行共 18.9 秒，验证 42 工具握手、Unicode 文本、UIA 开关、语义 Invoke 三条视觉路径均发生可比较变化，关闭来源窗口时正确回退桌面，且会话结束后无按键或鼠标残留。

### v0.35.0 — 原子剪贴板动作视觉验证

- `paste_text` 与 `copy_text` 现在复用统一语义动作包装；原有全部直接格式备份、真实 Ctrl+V/Ctrl+C、UIA Value/选区校验、重聚焦重试及成功/失败恢复顺序保持不变。
- 两项工具在原有返回数据上追加新的 `data.after_screenshot_id`、`data.visual_changed` 与精确 `data.visual_diff`，从而让原子文本传递也能直接续接动作后画面。
- 新增独立 `ClipboardVisual` 场景，没有运行旧剪贴板失败矩阵或其他场景。Release 构建与定向真实 WGC/MCP 运行共 15.6 秒，验证粘贴与全选复制均发生可比较视觉变化、剪贴板恢复为真、42 工具握手正常且会话无输入残留。

### v0.36.0 — 窗口管理动作视觉验证

- `activate_window`、`set_window_state` 与 `set_window_bounds` 现在以完整虚拟桌面作为动作前后视觉来源；激活、层级、最小化和几何变化因此共享稳定物理坐标系。
- 三项工具在保留既有前台、原生状态和精确 Win32 边界回读验证的同时，返回新的截图 ID、变化状态与精确差异；边界工具按现有 `ActionResult` 契约将字段放在 `data` 下。
- 新增独立 `WindowVisual` 场景，没有运行旧窗口套件。Release 构建与定向真实 WGC/MCP 运行共 12.6 秒，验证移动、恢复原边界、最小化和重新激活四条路径均发生可比较变化，42 工具握手正常且会话无输入残留。

### v0.37.0 — 应用启动视觉验证

- `launch_app` 现在纳入共享 UI 锁，在启动前后抓取完整虚拟桌面，并在既有进程 ID/输入就绪等待结果上追加新截图 ID、变化状态和精确差异；Process 句柄会及时释放而不终止目标。
- 启动画面只表示真实桌面观察，精确窗口就绪仍由返回的进程 ID 配合 `wait_for_window` 判定；`wait_ms=0` 或无界面目标允许视觉不变。
- 新增独立 `LaunchVisual` 场景，没有运行旧启动或窗口套件。Release 构建与定向真实 WGC/MCP 运行共 15.2 秒，验证第二个真实测试进程、独立顶层窗口和可比较启动变化，随后按 PID 清理，42 工具握手正常且会话无输入残留。

### v0.38.0 — 未绑定点击自动视觉基线

- `click` 的三条坐标路径现在都返回精确视觉差异：截图绑定路径继续复用权威前帧；未绑定窗口路径在激活后自动抓取选定窗口；无选择器的直接屏幕路径自动比较完整虚拟桌面。
- 自动基线只增强动作后验证，不放宽截图 ID 的年龄、目标身份或几何校验；已有 `window-and-screenshot-reobserve`、`desktop-screenshot-reobserve` 与 `screen-input-and-desktop-reobserve` 主策略保持兼容。
- 新增独立 `ClickVisual` 场景，没有运行旧像素输入套件。Release 构建与定向真实 WGC/MCP 运行共 17.1 秒，验证未绑定窗口 Save 点击和直接屏幕 Toggle 点击均发生可比较变化且语义结果到位，42 工具握手正常且会话无输入残留。

### v0.39.0 — 未绑定滚轮与拖拽自动视觉基线

- `scroll` 与自包含 `drag` 的三类坐标路径现在都返回精确视觉差异：截图 ID 继续作为权威前帧，未绑定窗口动作在激活后自动抓取选定窗口，直接屏幕动作自动比较完整虚拟桌面。
- 测试应用新增不改布局的滚轮状态反馈。首次定向运行暴露对 Win32 滚轮事件文本精确符号的断言不稳；改为先读取真实事件文本，再要求反向滚轮产生相反状态，产品输入语义未改。
- `MotionVisual` 最终定向运行耗时 10.1 秒；窗口/屏幕滚轮与窗口/屏幕自包含拖拽四条新路径均发生可比较变化、应用收到相应事件、42 工具握手正常且会话无输入残留。没有运行旧像素套件。

## Current boundaries and next work

- Windows OCR provides line/word grounding and bounded multi-scale local template matching covers known images. Novel non-text interpretation, rotation variation, and ambiguous scenes still depend on the calling model.
- Native Unicode typing remains the non-mutating default and `paste_text` is the atomic reversible fallback; callers using raw preserved writes must still restore their backup before `end_session` if the new clipboard content is not intentional.
- Remaining external matrix items are remote desktop, true multi-monitor/mixed-DPI hardware, protected windows, elevated-process boundaries, and longer state-changing workflows inside complex apps.
- Browser DOM and app-specific APIs are routing guidance for the calling agent, not implemented inside this Windows broker.

## Tooling notes

- The current Codex CLI exposes marketplace `add`, `upgrade`, and `remove`, but `upgrade` rejects local-path marketplaces because it only upgrades Git-backed sources. Validate local changes through the manifest plus cold MCP launcher gate; do not treat that expected CLI rejection as a plugin failure.
