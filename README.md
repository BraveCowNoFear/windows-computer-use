# Windows Computer Use

[简体中文](README.zh-CN.md)

A local Codex plugin for controlling Windows like a human, with semantic UI Automation first and native mouse, keyboard, capture, and OCR fallbacks. It is delivered as the combination the platform is designed for: a Codex skill, a local MCP server, and a native C# Windows broker.

The plugin runs in **full-control mode**. It does not maintain its own app allowlist, action blocklist, or confirmation matrix. It can address any interactive desktop window available to the current Windows user. Codex host permissions, process integrity, UAC secure desktop, and Windows policy remain outside the plugin and cannot be bypassed by it.

## What works today

- UI Automation 3 inspection through FlaUI, including names, AutomationIds, types, bounds, state, patterns, and focus.
- Stable session control ids with automatic re-location when UIA elements become stale.
- Semantic `invoke` and Unicode `enter_text`, with native SendInput fallback.
- Physical mouse click, drag, wheel, keyboard chords, app launching, window activation, and virtual-desktop coordinates.
- Window or desktop PNG capture through `PrintWindow` and screen-copy fallback.
- Native `Windows.Media.Ocr` using installed Windows language packs.
- Condition waits instead of blind sleeps and automatic re-observation after every action.
- A local stdio MCP with 16 tools and a current-user named-pipe broker.
- Compatibility with the global UI lock from `desktop-control-for-windows`.
- A real WinForms end-to-end test covering MCP handshake, UIA discovery, Chinese input, semantic invoke, condition wait, capture, OCR, and cleanup.

Windows Graphics Capture, GPU visual grounding, and a broad Office/WeChat/SolidWorks benchmark corpus are not yet implemented. The present capture layer is honest Win32 `PrintWindow`/screen copy, not WGC.

## Architecture

```mermaid
flowchart LR
    A["Codex task"] --> B["windows-computer-use skill"]
    B --> C["Local stdio MCP"]
    C -->|"current-user named pipe"| D["C# native broker"]
    D --> E["UIA3 semantic tree"]
    D --> F["Win32 capture and windows"]
    D --> G["Windows.Media.Ocr"]
    D --> H["SendInput mouse and keyboard"]
    E --> I["Target Windows app"]
    F --> I
    G --> I
    H --> I
```

The calling agent chooses the highest reliable layer: application API/browser DOM, UIA3, OCR, then physical pixels. The broker activates the exact selected window, serializes live UI access through the shared lock, performs the action, and re-observes the result. See [architecture.md](plugins/windows-computer-use/docs/architecture.md) and the [tool reference](plugins/windows-computer-use/skills/windows-computer-use/references/tool-reference.md).

## Build and test

Requirements: Windows 10/11, PowerShell 5.1+, and the .NET 8 SDK. The repository build script also detects the SDK installed at `C:\Users\Clr\.codex\tools\dotnet-sdk-8` on the development machine.

```powershell
cd "C:\path\to\windows-computer-use\plugins\windows-computer-use"
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1
```

`test.ps1` restores dependencies, builds and publishes the broker/MCP, runs the xUnit suite, opens the isolated WinForms test window, drives it through the real MCP, verifies OCR, closes the test app, and exits non-zero on any failed gate.

For a non-destructive real-app compatibility pass, run `scripts/real-app-smoke.ps1` after building. It opens isolated Notepad, File Explorer, and Settings windows, inspects/captures/OCRs them, and closes only windows whose ids were absent before the test.

## Local Codex plugin test

Build once, then add this repository root as a local marketplace:

```powershell
cd "C:\path\to\windows-computer-use\plugins\windows-computer-use"
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1

cd ..\..
codex plugin marketplace add .
```

Install **Windows Computer Use** from the local **Brave Cow Windows Tools** marketplace in Codex. The plugin's `.mcp.json` starts `scripts/run-mcp.ps1`, which launches the local MCP and native broker. For protocol-only testing, start that script directly and send newline-delimited MCP JSON-RPC on stdin.

## MCP surface

The 16 tools are `list_windows`, `launch_app`, `inspect_window`, `find_controls`, `invoke`, `enter_text`, `wait_for_ui`, `capture`, `ocr`, `click`, `press_key`, `type_text`, `scroll`, `drag`, `activate_window`, and `end_session`.

Use the exact window id returned by `list_windows`. Prefer stable control ids from `inspect_window`/`find_controls`; use fresh capture/OCR before coordinate actions. Coordinates are physical pixels and window-relative by default.

## Repository layout

```text
.agents/plugins/marketplace.json       local marketplace
plugins/windows-computer-use/
  .codex-plugin/plugin.json            Codex plugin manifest
  .mcp.json                            local MCP registration
  skills/windows-computer-use/         agent workflow and references
  src/WindowsComputerUse.Mcp/          MCP stdio host
  src/WindowsComputerUse.Broker/       UIA3/Win32/SendInput broker
  src/WindowsComputerUse.TestApp/      deterministic real-UI fixture
  scripts/                             build, launch, OCR, and E2E gates
  tests/                               protocol and native smoke tests
```

## License

MIT
