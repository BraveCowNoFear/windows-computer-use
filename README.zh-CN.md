# Windows Computer Use

[English](README.md)

这是一个让 Codex 像人类一样操作 Windows 的本地插件：优先使用 Windows UI Automation 语义控件，在语义层不足时降级到原生鼠标、键盘、截图与 OCR。项目按 Codex 官方插件形态交付，由 **Codex skill + 本地 MCP + C# Windows 原生 Broker** 三层组成。

插件默认运行在 **full-control（完全控制）模式**：自身不维护应用白名单、动作黑名单或二次确认矩阵；当前 Windows 用户能交互的桌面窗口，它都可以寻址和操作。Codex 宿主权限、进程完整性、UAC 安全桌面和 Windows 系统策略仍属于插件外部边界，插件无法绕过。

## 当前已实现

- 基于 FlaUI 的 UI Automation 3 语义检查：名称、AutomationId、控件类型、坐标、焦点、Value/只读/选择/开关/展开/滚动状态、文档文本与选中文本。
- 带 parent/depth/child 元数据的层级 UIA、路径稳定控件 ID、失效元素自动重定位，以及基于 observation ID 的增量差异。
- 语义 `invoke`、显式的 `perform_secondary_action` 聚焦/选择/开关/展开/折叠/滚动动作与 Unicode `enter_text`，失败后降级到原生 SendInput。
- 物理鼠标位置读取、平滑移动/悬停、五键鼠标点击/拖拽/滚轮、可跨动作保持的鼠标按下/释放、带隐式修饰键与重复时序的组合键、可跟踪的键盘按下/释放、应用启动、窗口激活和虚拟桌面坐标。
- 物理多屏拓扑：每块显示器的边界、工作区、有效 DPI、主屏标记和缩放百分比。
- 无系统选框的原生 Windows Graphics Capture 窗口 PNG 捕获，并保留 `PrintWindow` 与屏幕复制降级。
- 以 DWM 可见边界对齐 WGC，并显式支持 `window` / `screen` / `screenshot` 坐标空间，避免不可见缩放边框造成点击偏移。
- 原生 `Windows.Media.Ocr` 行/词边界、带截图 ID 的新鲜 OCR，以及可直接衔接点击的 `find_text` 文本定位。
- 本地等比例 PNG/JPEG 模板匹配：粗到细采样颜色评分、重叠抑制与新鲜截图绑定坐标，用于无文字图标和画布目标。
- 窗口 owner/root-owner 关系、用于瞬态弹窗的 `wait_for_window`，以及带验证的最小化/最大化/还原控制。
- 状态条件等待代替盲目 sleep：除存在/可见/焦点外，还支持 Value 等值/包含、选中/未选中、Toggle、展开/折叠与只读/可编辑谓词；每次动作后自动重新观测验证。
- 一次调用同时返回 UIA 与画面的 `snapshot`，带时间、截图 ID 和 SHA-256；窗口移动、缩放或截图过期后拒绝继续盲点。
- 30 个工具的本地 stdio MCP，以及仅当前用户可连接的命名管道 Broker。
- 与 `desktop-control-for-windows` 共用全局 UI 锁协议。
- 真实 WinForms 端到端测试：MCP 握手、UIA 发现、丰富状态差异、选中文本、二级动作、中文输入、语义 Invoke、状态等待、截图、OCR、跨动作键鼠手势与清理。

本地视觉语言模型仍未实现；已知的无文字目标可用等比例模板匹配，陌生视觉仍由 OCR 与模型侧图像推理理解。扩展兼容门禁已覆盖 Word、Excel、VS Code/Electron、微信和 SolidWorks；真正的多屏/混合 DPI 与高完整性边界仍需对应硬件和进程状态。

## 架构

```mermaid
flowchart LR
    A["Codex 任务"] --> B["windows-computer-use skill"]
    B --> C["本地 stdio MCP"]
    C -->|"当前用户命名管道"| D["C# 原生 Broker"]
    D --> E["UIA3 语义树"]
    D --> F["Windows Graphics Capture + Win32"]
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

`test.ps1` 会先校验 Codex 清单、marketplace 路径、MCP 启动器、skill/资源和全部 PowerShell 语法，再还原依赖、构建并发布 Broker/MCP、运行 xUnit、打开隔离的 WinForms 测试窗口，并硬性验证层级 UIA/状态差异、Value 与选中文本、语义二级动作、状态条件等待、键盘保持/隐式修饰键、WGC、OCR 与图像模板定位、快照新鲜度和过期坐标拒绝；任一门禁失败都会返回非零退出码。

构建后还可运行 `scripts/real-app-smoke.ps1` 做非破坏性真实应用兼容测试：它会打开隔离的记事本、资源管理器和设置窗口，完成 UIA/截图/OCR，并且只关闭测试前不存在的窗口 ID。

运行 `scripts/extended-app-smoke.ps1` 可覆盖隔离的 Word、Excel 和 VS Code/Electron；追加 `-IncludeWeChat -IncludeSolidWorks` 覆盖已安装的微信与 SolidWorks，`-OptionalOnly` 只跑可选应用。已有同类进程会被跳过保护，测试只读取 UIA/WGC/OCR，并只关闭本次新建的进程组。

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

30 个工具分别是：`list_windows`、`display_info`、`pointer_position`、`launch_app`、`wait_for_window`、`inspect_window`、`observe_changes`、`find_controls`、`invoke`、`perform_secondary_action`、`enter_text`、`wait_for_ui`、`capture`、`snapshot`、`ocr`、`find_text`、`find_image`、`move_pointer`、`click`、`mouse_down`、`mouse_up`、`press_key`、`key_down`、`key_up`、`type_text`、`scroll`、`drag`、`set_window_state`、`activate_window`、`end_session`。

窗口应使用 `list_windows` 返回的精确 ID；即使窗口暂时隐藏或无标题，Broker 也会直接按 HWND 恢复描述。如果应用重建 HWND，只有进程 ID、原生窗口类和非空标题仍一致且替代窗口唯一时，旧 ID 才会自动跟随；否则拒绝模糊匹配并要求重新选择。瞬态弹窗用 `wait_for_window` 配合 `owner_window_id` 定位。操作优先使用 `inspect_window`/`find_controls` 返回的稳定控件 ID 和当前状态字段，状态切换后可把旧 `observation_id` 交给 `observe_changes`；需要比主 Invoke 更明确的 UIA 动作时使用 `perform_secondary_action`。确需坐标时，文字用 `find_text`、已知等比例本地图像用 `find_image`、陌生画面用 `snapshot`，再把返回的 `screenshot_id` 与 `coordinate_space: "screenshot"` 交给坐标动作；语义/输入动作会让旧截图失效，目标移动、缩放或超过 `max_age_ms` 后 Broker 也会拒绝盲点。视觉观察前应先还原最小化窗口。旧调用仍默认使用相对窗口的物理像素。

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
