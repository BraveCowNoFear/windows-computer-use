# Windows Computer Use

[English](README.md)

这是一个让 Codex 像人类一样操作 Windows 的本地插件：优先使用 Windows UI Automation 语义控件，在语义层不足时降级到原生鼠标、键盘、截图与 OCR。项目按 Codex 官方插件形态交付，由 **Codex skill + 本地 MCP + C# Windows 原生 Broker** 三层组成。

插件默认运行在 **full-control（完全控制）模式**：自身不维护应用白名单、动作黑名单或二次确认矩阵；当前 Windows 用户能交互的桌面窗口，它都可以寻址和操作。Codex 宿主权限、进程完整性、UAC 安全桌面和 Windows 系统策略仍属于插件外部边界，插件无法绕过。

## 当前已实现

- 基于 FlaUI 的 UI Automation 3 语义检查：名称、AutomationId、控件类型、坐标、状态、Pattern 与焦点。
- 会话内稳定控件 ID；UIA 元素失效后会按语义选择器自动重新定位。
- 语义 `invoke` 与 Unicode `enter_text`，失败后降级到原生 SendInput。
- 物理像素点击、拖拽、滚轮、组合键、应用启动、窗口激活和虚拟桌面坐标。
- `PrintWindow` 窗口 PNG 捕获与屏幕复制降级。
- 使用已安装 Windows 语言包的原生 `Windows.Media.Ocr`。
- 条件等待代替盲目 sleep；每次动作后自动重新观测验证。
- 16 个工具的本地 stdio MCP，以及仅当前用户可连接的命名管道 Broker。
- 与 `desktop-control-for-windows` 共用全局 UI 锁协议。
- 真实 WinForms 端到端测试：MCP 握手、UIA 发现、中文输入、语义 Invoke、状态等待、截图、OCR 与清理。

Windows Graphics Capture、GPU 视觉定位，以及覆盖 Office、微信、SolidWorks 的大规模基准仍未完成。当前捕获层如实采用 Win32 `PrintWindow`/屏幕复制，并未冒充 WGC。

## 架构

```mermaid
flowchart LR
    A["Codex 任务"] --> B["windows-computer-use skill"]
    B --> C["本地 stdio MCP"]
    C -->|"当前用户命名管道"| D["C# 原生 Broker"]
    D --> E["UIA3 语义树"]
    D --> F["Win32 截图与窗口"]
    D --> G["Windows.Media.Ocr"]
    D --> H["SendInput 鼠标键盘"]
    E --> I["目标 Windows 应用"]
    F --> I
    G --> I
    H --> I
```

调用方按可靠性选择最高层：应用 API/浏览器 DOM → UIA3 → OCR → 物理像素。Broker 会激活精确目标窗口，通过共享锁串行化实时桌面访问，执行动作，并重新观测结果。详见 [架构说明](plugins/windows-computer-use/docs/architecture.md) 与 [工具参考](plugins/windows-computer-use/skills/windows-computer-use/references/tool-reference.md)。

## 构建与测试

要求：Windows 10/11、PowerShell 5.1+、.NET 8 SDK。仓库脚本也会识别开发机上的 `C:\Users\Clr\.codex\tools\dotnet-sdk-8` SDK。

```powershell
cd "C:\path\to\windows-computer-use\plugins\windows-computer-use"
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1
```

`test.ps1` 会还原依赖、构建并发布 Broker/MCP、运行 xUnit、打开隔离的 WinForms 测试窗口、通过真实 MCP 驱动它、验证 OCR、关闭测试应用，并在任一门禁失败时返回非零退出码。

构建后还可运行 `scripts/real-app-smoke.ps1` 做非破坏性真实应用兼容测试：它会打开隔离的记事本、资源管理器和设置窗口，完成 UIA/截图/OCR，并且只关闭测试前不存在的窗口 ID。

## 在 Codex 本地安装测试

先构建，再把仓库根目录加入本地 marketplace：

```powershell
cd "C:\path\to\windows-computer-use\plugins\windows-computer-use"
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1

cd ..\..
codex plugin marketplace add .
```

随后在 Codex 的本地 **Brave Cow Windows Tools** marketplace 中安装 **Windows Computer Use**。插件 `.mcp.json` 会启动 `scripts/run-mcp.ps1`，再拉起本地 MCP 与原生 Broker。只测试协议时，也可以直接运行该脚本，并从 stdin 输入逐行 MCP JSON-RPC。

## MCP 工具面

16 个工具分别是：`list_windows`、`launch_app`、`inspect_window`、`find_controls`、`invoke`、`enter_text`、`wait_for_ui`、`capture`、`ocr`、`click`、`press_key`、`type_text`、`scroll`、`drag`、`activate_window`、`end_session`。

窗口应使用 `list_windows` 返回的精确 ID；操作优先使用 `inspect_window`/`find_controls` 返回的稳定控件 ID；坐标操作前重新截图/OCR。坐标统一为物理像素，默认相对目标窗口。

## 仓库结构

```text
.agents/plugins/marketplace.json       本地 marketplace
plugins/windows-computer-use/
  .codex-plugin/plugin.json            Codex 插件清单
  .mcp.json                            本地 MCP 注册
  skills/windows-computer-use/         Agent 工作流与参考
  src/WindowsComputerUse.Mcp/          MCP stdio 主机
  src/WindowsComputerUse.Broker/       UIA3/Win32/SendInput Broker
  src/WindowsComputerUse.TestApp/      可重复的真实 UI 测试应用
  scripts/                             构建、启动、OCR 与端到端门禁
  tests/                               协议与原生冒烟测试
```

## 许可证

MIT
