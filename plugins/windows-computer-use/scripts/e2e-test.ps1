param([switch]$KeepTestWindow, [switch]$KeepArtifacts, [switch]$RequireWgc)

$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$mcpPath = Join-Path $pluginRoot 'dist\win-x64\mcp\WindowsComputerUse.Mcp.exe'
$brokerPath = Join-Path $pluginRoot 'dist\win-x64\broker\WindowsComputerUse.Broker.exe'
$testAppPath = Join-Path $pluginRoot 'src\WindowsComputerUse.TestApp\bin\Release\net8.0-windows7.0\WindowsComputerUse.TestApp.exe'
foreach ($requiredPath in @($mcpPath, $brokerPath, $testAppPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) { throw "Missing build output: $requiredPath. Run scripts/build.ps1 first." }
}

$testApp = $null
$occluder = $null
$mcp = $null
$mcpErrorTask = $null
$capturePath = Join-Path $env:TEMP ("windows-computer-use-e2e-{0}.png" -f [guid]::NewGuid().ToString('N'))
$visualSourcePath = Join-Path $env:TEMP ("windows-computer-use-visual-source-{0}.png" -f [guid]::NewGuid().ToString('N'))
$templatePath = Join-Path $env:TEMP ("windows-computer-use-template-{0}.png" -f [guid]::NewGuid().ToString('N'))
$scaledTemplatePath = Join-Path $env:TEMP ("windows-computer-use-scaled-template-{0}.png" -f [guid]::NewGuid().ToString('N'))
$nextId = 0
$clipboardBackupId = $null
$clipboardRoundtrip = $false
$atomicPasteRoundtrip = $false
$atomicPasteAppend = $false
$atomicPasteFailureRestore = $false
$atomicCopyAll = $false
$atomicCopyCurrent = $false
$atomicCopyFailureRestore = $false

function Invoke-McpRequest {
    param([string]$Method, [hashtable]$Params = @{})
    $script:nextId++
    $payload = [ordered]@{ jsonrpc = '2.0'; id = $script:nextId; method = $Method; params = $Params }
    $script:mcp.StandardInput.WriteLine(($payload | ConvertTo-Json -Depth 20 -Compress))
    $script:mcp.StandardInput.Flush()
    $line = $script:mcp.StandardOutput.ReadLine()
    if ($null -eq $line) {
        $stderr = if ($null -ne $script:mcpErrorTask -and $script:mcpErrorTask.IsCompleted) { $script:mcpErrorTask.GetAwaiter().GetResult() } else { 'MCP stderr is still draining.' }
        throw "MCP process closed before replying. $stderr"
    }
    $response = $line | ConvertFrom-Json
    if ($null -ne $response.error) { throw "MCP error: $($response.error.message)" }
    return $response.result
}

function Invoke-WcuTool {
    param([string]$Name, [hashtable]$Arguments = @{})
    $result = Invoke-McpRequest -Method 'tools/call' -Params @{ name = $Name; arguments = $Arguments }
    if ($result.isError) { throw "Stage $script:stage; tool $Name failed: $($result.content[0].text)" }
    if ($Name -in @('capture', 'snapshot', 'observe_desktop')) { return $result }
    return ($result.content[0].text | ConvertFrom-Json)
}

try {
    $testApp = Start-Process -FilePath $testAppPath -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 100
        $testApp.Refresh()
    } until ($testApp.MainWindowHandle -ne 0 -or [DateTime]::UtcNow -ge $deadline)
    if ($testApp.MainWindowHandle -eq 0) { throw 'Test app did not create a visible window.' }

    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $mcpPath
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.EnvironmentVariables['WCU_BROKER_PATH'] = $brokerPath
    $start.EnvironmentVariables['WCU_PLUGIN_ROOT'] = $pluginRoot
    $start.EnvironmentVariables['WCU_DEBUG'] = '1'
    if ($RequireWgc) { $start.EnvironmentVariables['WCU_REQUIRE_WGC'] = '1' }
    $mcp = [System.Diagnostics.Process]::new()
    $mcp.StartInfo = $start
    if (-not $mcp.Start()) { throw 'Could not start MCP process.' }
    $mcpErrorTask = $mcp.StandardError.ReadToEndAsync()

    $initialize = Invoke-McpRequest -Method 'initialize' -Params @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'e2e-test'; version = '1.0' } }
    if ($initialize.serverInfo.name -ne 'windows-computer-use') { throw 'Unexpected MCP server identity.' }
    $tools = Invoke-McpRequest -Method 'tools/list'
    if (@($tools.tools).Count -ne 37) { throw "Expected 37 MCP tools, found $(@($tools.tools).Count)." }

    $displayInfo = Invoke-WcuTool -Name 'display_info'
    if (@($displayInfo.displays).Count -lt 1 -or $displayInfo.virtualDesktop.width -lt 1 -or $displayInfo.displays[0].dpiX -lt 96) {
        throw 'Physical display topology or DPI metadata is incomplete.'
    }

    $script:stage = 'desktop-observation'
    $desktopObservation = Invoke-WcuTool -Name 'observe_desktop'
    if (@($desktopObservation.content).Count -ne 2 -or $desktopObservation.content[1].type -ne 'image' -or $desktopObservation.content[1].mimeType -ne 'image/png' -or -not $desktopObservation.content[1].data) {
        throw 'Atomic desktop observation did not return text metadata plus one PNG image.'
    }
    $desktopObservationMeta = $desktopObservation.content[0].text | ConvertFrom-Json
    $observedTarget = @($desktopObservationMeta.windows | Where-Object { $_.title -eq 'Windows Computer Use Test App' })
    if ($observedTarget.Count -ne 1 -or
        @($desktopObservationMeta.topology.displays).Count -lt 1 -or
        $desktopObservationMeta.capture.width -ne $desktopObservationMeta.topology.virtualDesktop.width -or
        $desktopObservationMeta.capture.height -ne $desktopObservationMeta.topology.virtualDesktop.height -or
        -not $desktopObservationMeta.capture.id -or
        $desktopObservationMeta.pointer.coordinateSpace -ne 'physical-screen-pixels' -or
        $desktopObservationMeta.pointer.x -lt $desktopObservationMeta.topology.virtualDesktop.x -or
        $desktopObservationMeta.pointer.y -lt $desktopObservationMeta.topology.virtualDesktop.y -or
        $desktopObservationMeta.pointer.x -ge ($desktopObservationMeta.topology.virtualDesktop.x + $desktopObservationMeta.topology.virtualDesktop.width) -or
        $desktopObservationMeta.pointer.y -ge ($desktopObservationMeta.topology.virtualDesktop.y + $desktopObservationMeta.topology.virtualDesktop.height)) {
        throw "Atomic desktop observation metadata was incomplete: $($desktopObservationMeta | ConvertTo-Json -Depth 5 -Compress)"
    }
    $observationMoveX = [int]$observedTarget[0].bounds.x + 20 - [int]$desktopObservationMeta.capture.bounds.x
    $observationMoveY = [int]$observedTarget[0].bounds.y + 20 - [int]$desktopObservationMeta.capture.bounds.y
    $observationMove = Invoke-WcuTool -Name 'move_pointer' -Arguments @{ x = $observationMoveX; y = $observationMoveY; coordinate_space = 'screenshot'; screenshot_id = $desktopObservationMeta.capture.id }
    if (-not $observationMove.ok -or $observationMove.coordinate_space -ne 'screenshot') {
        throw 'Atomic desktop observation screenshot id was not actionable.'
    }

    $windows = Invoke-WcuTool -Name 'list_windows'
    $target = @($windows.windows | Where-Object { $_.title -eq 'Windows Computer Use Test App' })
    if ($target.Count -ne 1) { throw "Expected one test window, found $($target.Count)." }
    $windowId = [long]$target[0].id

    $script:stage = 'window-bounds'
    $originalBounds = $target[0].bounds
    $movedX = [int]$originalBounds.x + 20
    $movedY = [int]$originalBounds.y + 20
    $movedWidth = [int]$originalBounds.width + 40
    $movedHeight = [int]$originalBounds.height + 30
    $movedBounds = Invoke-WcuTool -Name 'set_window_bounds' -Arguments @{ window_id = $windowId; x = $movedX; y = $movedY; width = $movedWidth; height = $movedHeight; activate = $false }
    if (-not $movedBounds.ok -or
        $movedBounds.backend -ne 'win32-set-window-pos' -or
        -not $movedBounds.verification.verified -or
        $movedBounds.data.window.bounds.x -ne $movedX -or
        $movedBounds.data.window.bounds.y -ne $movedY -or
        $movedBounds.data.window.bounds.width -ne $movedWidth -or
        $movedBounds.data.window.bounds.height -ne $movedHeight -or
        $movedBounds.data.window.isForeground -ne $target[0].isForeground) {
        throw "Native window move/resize did not reach the exact physical bounds without changing foreground state: $($movedBounds | ConvertTo-Json -Depth 6 -Compress)"
    }
    $restoredBounds = Invoke-WcuTool -Name 'set_window_bounds' -Arguments @{ window_id = $windowId; x = [int]$originalBounds.x; y = [int]$originalBounds.y; width = [int]$originalBounds.width; height = [int]$originalBounds.height; activate = $false }
    if ($restoredBounds.data.window.bounds.x -ne $originalBounds.x -or
        $restoredBounds.data.window.bounds.y -ne $originalBounds.y -or
        $restoredBounds.data.window.bounds.width -ne $originalBounds.width -or
        $restoredBounds.data.window.bounds.height -ne $originalBounds.height) {
        throw 'Native window geometry did not restore the exact original rectangle.'
    }

    $inspection = Invoke-WcuTool -Name 'inspect_window' -Arguments @{ window_id = $windowId; limit = 100 }
    if (@($inspection.controls).Count -lt 4) { throw 'UIA inspection returned too few controls.' }

    $script:stage = 'initial-input'
    $input = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; automation_id = 'InputBox'; limit = 2 }
    if ($input.count -ne 1) {
        $input = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; name = 'Input'; control_type = 'Edit'; limit = 2 }
    }
    if ($input.count -ne 1) { throw 'Could not uniquely resolve the semantic input control.' }

    $testText = 'Codex ' + [char]0x4F60 + [char]0x597D
    $entered = Invoke-WcuTool -Name 'enter_text' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; text = $testText }
    if (-not $entered.ok -or -not $entered.verification.verified) { throw 'Text entry did not verify.' }
    $inputState = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; automation_id = 'InputBox'; limit = 2 }
    if ($inputState.count -ne 1 -or $inputState.controls[0].value -ne $testText -or $inputState.controls[0].isReadOnly) {
        throw 'UIA ValuePattern state was not exposed after text entry.'
    }
    $valueWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; state = 'value_equals'; expected_value = $testText; timeout_ms = 1500 }
    if (-not $valueWait.matched) { throw "Value-equality condition wait did not match the edit state: $($valueWait | ConvertTo-Json -Depth 5 -Compress)" }
    Invoke-WcuTool -Name 'perform_secondary_action' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; action = 'focus' } | Out-Null
    $testApp.Refresh()
    $afterFocusWindows = Invoke-WcuTool -Name 'list_windows'
    if ($testApp.HasExited -or @($afterFocusWindows.windows | Where-Object { $_.id -eq $windowId }).Count -ne 1) {
        $exitDetails = if ($testApp.HasExited) { "exit_code=$($testApp.ExitCode)" } else { "new_handle=$($testApp.MainWindowHandle)" }
        throw "Target window changed after UIA focus ($exitDetails): $($afterFocusWindows | ConvertTo-Json -Depth 4 -Compress)"
    }
    Invoke-WcuTool -Name 'press_key' -Arguments @{ window_id = $windowId; key = 'ctrl+a' } | Out-Null
    $textSelectionState = Invoke-WcuTool -Name 'inspect_window' -Arguments @{ window_id = $windowId; limit = 150 }
    if ($textSelectionState.selectedText -ne $testText -or -not $textSelectionState.documentText) {
        throw "Focused TextPattern state was not exposed: $($textSelectionState | ConvertTo-Json -Depth 4 -Compress)"
    }

    $script:stage = 'clipboard-roundtrip'
    $originalClipboard = Invoke-WcuTool -Name 'read_clipboard_text'
    $clipboardText = "WCU clipboard $([guid]::NewGuid().ToString('N'))"
    $clipboardWrite = Invoke-WcuTool -Name 'write_clipboard_text' -Arguments @{ text = $clipboardText; preserve_previous = $true }
    $clipboardBackupId = [string]$clipboardWrite.backup_id
    if (-not $clipboardWrite.ok -or -not $clipboardBackupId -or $clipboardWrite.sha256 -eq $null) {
        throw 'Native clipboard write did not return a verified backup token and content digest.'
    }
    try {
        $clipboardRead = Invoke-WcuTool -Name 'read_clipboard_text'
        if (-not $clipboardRead.contains_text -or $clipboardRead.text -ne $clipboardText -or $clipboardRead.sha256 -ne $clipboardWrite.sha256) {
            throw 'Native clipboard read did not reproduce the written Unicode text.'
        }
        Invoke-WcuTool -Name 'enter_text' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; text = '' } | Out-Null
        Invoke-WcuTool -Name 'perform_secondary_action' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; action = 'focus' } | Out-Null
        Invoke-WcuTool -Name 'press_key' -Arguments @{ window_id = $windowId; key = 'ctrl+v' } | Out-Null
        $clipboardPasteWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; state = 'value_equals'; expected_value = $clipboardText; timeout_ms = 2000 }
        if (-not $clipboardPasteWait.matched) { throw 'Real Ctrl+V did not paste the native clipboard text into the semantic edit control.' }
    } finally {
        if ($clipboardBackupId) {
            $clipboardRestore = Invoke-WcuTool -Name 'restore_clipboard' -Arguments @{ backup_id = $clipboardBackupId }
            $clipboardBackupId = $null
            $missingFormats = @($originalClipboard.formats | Where-Object { @($clipboardRestore.formats) -notcontains $_ })
            if (-not $clipboardRestore.ok -or
                $clipboardRestore.contains_text -ne $originalClipboard.contains_text -or
                $clipboardRestore.normalized_sha256 -ne $originalClipboard.normalized_sha256 -or
                $missingFormats.Count -ne 0) {
                throw 'Clipboard restoration did not reproduce the original text state and direct formats.'
            }
        }
    }
    $clipboardRoundtrip = $true

    $atomicPasteText = "Atomic paste $([guid]::NewGuid().ToString('N'))"
    $atomicPaste = Invoke-WcuTool -Name 'paste_text' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; text = $atomicPasteText; timeout_ms = 2000 }
    if (-not $atomicPaste.ok -or -not $atomicPaste.data.clipboard_restored -or $atomicPaste.verification.strategy -ne 'uia3-value-and-clipboard-restore' -or $atomicPaste.data.control.value -ne $atomicPasteText) {
        throw 'Atomic clipboard-backed paste did not verify target Value and clipboard restoration.'
    }
    $atomicAppendText = ' + appended'
    $atomicAppend = Invoke-WcuTool -Name 'paste_text' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; text = $atomicAppendText; append = $true; timeout_ms = 2000 }
    if (-not $atomicAppend.ok -or -not $atomicAppend.data.clipboard_restored -or -not $atomicAppend.data.append -or $atomicAppend.data.control.value -ne ($atomicPasteText + $atomicAppendText)) {
        throw 'Atomic clipboard-backed append did not move to the end, verify Value, and restore the clipboard.'
    }
    $atomicPasteAppend = $true
    $afterAtomicClipboard = Invoke-WcuTool -Name 'read_clipboard_text'
    $missingAtomicFormats = @($originalClipboard.formats | Where-Object { @($afterAtomicClipboard.formats) -notcontains $_ })
    if ($afterAtomicClipboard.contains_text -ne $originalClipboard.contains_text -or $afterAtomicClipboard.normalized_sha256 -ne $originalClipboard.normalized_sha256 -or $missingAtomicFormats.Count -ne 0) {
        throw 'Atomic paste did not preserve the original clipboard state.'
    }
    $atomicPasteRoundtrip = $true

    $readOnlyPaste = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; automation_id = 'ReadOnlyPasteBox'; limit = 2 }
    if ($readOnlyPaste.count -ne 1 -or -not $readOnlyPaste.controls[0].isReadOnly) { throw 'Could not resolve the deterministic read-only paste target.' }
    $pasteFailureObserved = $false
    try {
        Invoke-WcuTool -Name 'paste_text' -Arguments @{ window_id = $windowId; control_id = $readOnlyPaste.controls[0].id; text = 'must not stick'; timeout_ms = 150 } | Out-Null
    } catch {
        if ($_.Exception.Message -match 'Value did not reach the expected text') { $pasteFailureObserved = $true } else { throw }
    }
    if (-not $pasteFailureObserved) { throw 'Read-only atomic paste did not fail its Value verification.' }
    $afterFailedPasteClipboard = Invoke-WcuTool -Name 'read_clipboard_text'
    $missingFailedFormats = @($originalClipboard.formats | Where-Object { @($afterFailedPasteClipboard.formats) -notcontains $_ })
    if ($afterFailedPasteClipboard.contains_text -ne $originalClipboard.contains_text -or $afterFailedPasteClipboard.normalized_sha256 -ne $originalClipboard.normalized_sha256 -or $missingFailedFormats.Count -ne 0) {
        throw 'Atomic paste failure did not restore the original clipboard state.'
    }
    $atomicPasteFailureRestore = $true

    $expectedCopiedText = $atomicPasteText + $atomicAppendText
    $script:stage = 'atomic-copy-all'
    $copyAll = Invoke-WcuTool -Name 'copy_text' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; selection = 'all'; timeout_ms = 2000 }
    if (-not $copyAll.ok -or -not $copyAll.data.clipboard_restored -or $copyAll.data.text -ne $expectedCopiedText -or $copyAll.verification.strategy -ne 'clipboard-sequence-and-uia-selection') {
        throw 'Atomic select-all copy did not return the UIA-selected text and restore the clipboard.'
    }
    $atomicCopyAll = $true
    $script:stage = 'atomic-copy-current'
    $copyCurrent = Invoke-WcuTool -Name 'copy_text' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; selection = 'current'; timeout_ms = 2000 }
    if (-not $copyCurrent.ok -or -not $copyCurrent.data.clipboard_restored -or $copyCurrent.data.text -ne $expectedCopiedText) {
        throw 'Atomic current-selection copy did not return the selected text and restore the clipboard.'
    }
    $atomicCopyCurrent = $true
    $afterAtomicCopyClipboard = Invoke-WcuTool -Name 'read_clipboard_text'
    $missingCopyFormats = @($originalClipboard.formats | Where-Object { @($afterAtomicCopyClipboard.formats) -notcontains $_ })
    if ($afterAtomicCopyClipboard.contains_text -ne $originalClipboard.contains_text -or $afterAtomicCopyClipboard.normalized_sha256 -ne $originalClipboard.normalized_sha256 -or $missingCopyFormats.Count -ne 0) {
        throw 'Atomic copy did not preserve the original clipboard state.'
    }

    $copyFailureTarget = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; automation_id = 'CommitButton'; limit = 2 }
    if ($copyFailureTarget.count -ne 1) { throw 'Could not resolve the deterministic copy failure target.' }
    $copyFailureObserved = $false
    $script:stage = 'atomic-copy-failure'
    try {
        Invoke-WcuTool -Name 'copy_text' -Arguments @{ window_id = $windowId; control_id = $copyFailureTarget.controls[0].id; selection = 'current'; timeout_ms = 150 } | Out-Null
    } catch {
        if ($_.Exception.Message -match 'did not change the clipboard') { $copyFailureObserved = $true } else { throw }
    }
    if (-not $copyFailureObserved) { throw 'Atomic copy from a non-text button did not fail its clipboard sequence gate.' }
    $afterFailedCopyClipboard = Invoke-WcuTool -Name 'read_clipboard_text'
    $missingFailedCopyFormats = @($originalClipboard.formats | Where-Object { @($afterFailedCopyClipboard.formats) -notcontains $_ })
    if ($afterFailedCopyClipboard.contains_text -ne $originalClipboard.contains_text -or $afterFailedCopyClipboard.normalized_sha256 -ne $originalClipboard.normalized_sha256 -or $missingFailedCopyFormats.Count -ne 0) {
        throw "Atomic copy failure did not restore the original clipboard state: expected_contains=$($originalClipboard.contains_text), actual_contains=$($afterFailedCopyClipboard.contains_text), expected_hash=$($originalClipboard.normalized_sha256), actual_hash=$($afterFailedCopyClipboard.normalized_sha256), missing_formats=$($missingFailedCopyFormats -join ',')."
    }
    $atomicCopyFailureRestore = $true

    Invoke-WcuTool -Name 'enter_text' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; text = $testText } | Out-Null
    Invoke-WcuTool -Name 'perform_secondary_action' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; action = 'focus' } | Out-Null
    Invoke-WcuTool -Name 'press_key' -Arguments @{ window_id = $windowId; key = 'ctrl+a' } | Out-Null

    $script:stage = 'keyboard-state'
    $heldShift = Invoke-WcuTool -Name 'key_down' -Arguments @{ window_id = $windowId; key = 'shift' }
    if (@($heldShift.data.held_keys) -notcontains 'shift') { throw 'key_down did not report the held Shift key.' }
    $shiftDownWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; name = 'Key down: ShiftKey'; state = 'exists'; timeout_ms = 1500 }
    if (-not $shiftDownWait.matched) { throw 'The test app did not observe the held Shift key.' }
    $releasedShift = Invoke-WcuTool -Name 'key_up' -Arguments @{ window_id = $windowId; key = 'shift' }
    if (@($releasedShift.data.held_keys).Count -ne 0) { throw 'key_up left a tracked key held.' }
    $shiftUpWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; name = 'Key up: ShiftKey'; state = 'exists'; timeout_ms = 1500 }
    if (-not $shiftUpWait.matched) { throw 'The test app did not observe the Shift key release.' }
    Invoke-WcuTool -Name 'press_key' -Arguments @{ window_id = $windowId; key = 'A'; repeat = 2; interval_ms = 10 } | Out-Null
    $uppercaseWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; state = 'value_equals'; expected_value = 'AA'; timeout_ms = 1500 }
    if (-not $uppercaseWait.matched) { throw 'Printable uppercase repeat did not apply its implied Shift modifier.' }
    Invoke-WcuTool -Name 'enter_text' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; text = $testText } | Out-Null

    $script:stage = 'desktop-keyboard'
    Invoke-WcuTool -Name 'perform_secondary_action' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; action = 'focus' } | Out-Null
    $desktopSelect = Invoke-WcuTool -Name 'press_key' -Arguments @{ desktop = $true; key = 'ctrl+a' }
    if ($desktopSelect.backend -ne 'sendinput-current-foreground' -or -not $desktopSelect.data.desktop -or [long]$desktopSelect.data.foreground_before.id -ne $windowId) {
        throw 'Desktop keypress did not preserve and report the current foreground window.'
    }
    $desktopText = 'Desktop ' + [char]0x952E + [char]0x76D8
    $desktopTyped = Invoke-WcuTool -Name 'type_text' -Arguments @{ desktop = $true; text = $desktopText }
    if ($desktopTyped.backend -ne 'sendinput-unicode-current-foreground' -or -not $desktopTyped.data.desktop -or [long]$desktopTyped.data.foreground_after.id -ne $windowId) {
        throw 'Desktop Unicode typing did not retain the current foreground window.'
    }
    $desktopTextWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; state = 'value_equals'; expected_value = $desktopText; timeout_ms = 1500 }
    if (-not $desktopTextWait.matched) { throw 'Desktop Unicode typing did not reach the existing focused control.' }
    $desktopHeld = Invoke-WcuTool -Name 'key_down' -Arguments @{ desktop = $true; key = 'shift' }
    if (-not $desktopHeld.data.desktop -or @($desktopHeld.data.held_keys) -notcontains 'shift') { throw 'Desktop key_down did not track Shift.' }
    $desktopShiftDownWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; name = 'Key down: ShiftKey'; state = 'exists'; timeout_ms = 1500 }
    if (-not $desktopShiftDownWait.matched) { throw 'The focused test app did not observe desktop Shift down.' }
    $desktopReleased = Invoke-WcuTool -Name 'key_up' -Arguments @{ desktop = $true; key = 'shift' }
    if (-not $desktopReleased.data.desktop -or @($desktopReleased.data.held_keys).Count -ne 0) { throw 'Desktop key_up did not release Shift.' }
    $desktopShiftUpWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; name = 'Key up: ShiftKey'; state = 'exists'; timeout_ms = 1500 }
    if (-not $desktopShiftUpWait.matched) { throw 'The focused test app did not observe desktop Shift up.' }
    $desktopConflictRejected = $false
    try {
        Invoke-WcuTool -Name 'press_key' -Arguments @{ desktop = $true; window_id = $windowId; key = 'escape' } | Out-Null
    } catch {
        if ($_.Exception.Message -match 'desktop=true cannot be combined with a window selector') { $desktopConflictRejected = $true } else { throw }
    }
    if (-not $desktopConflictRejected) { throw 'Desktop keyboard mode accepted a conflicting window selector.' }
    Invoke-WcuTool -Name 'enter_text' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; text = $testText } | Out-Null

    $script:stage = 'mouse-state'
    $mouseSurface = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; automation_id = 'MouseSurface'; limit = 2 }
    if ($mouseSurface.count -ne 1) { throw 'Could not uniquely resolve the mouse interaction surface.' }
    $mouseX = [int]$mouseSurface.controls[0].bounds.x + [int]($mouseSurface.controls[0].bounds.width / 2)
    $mouseY = [int]$mouseSurface.controls[0].bounds.y + [int]($mouseSurface.controls[0].bounds.height / 2)
    $heldMouse = Invoke-WcuTool -Name 'mouse_down' -Arguments @{ window_id = $windowId; x = $mouseX; y = $mouseY; coordinate_space = 'screen'; button = 'left' }
    if (@($heldMouse.data.held_buttons) -notcontains 'left' -or -not $heldMouse.verification.verified) { throw 'mouse_down did not report and verify the held left button.' }
    $mouseDownWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; name = 'Mouse down: Left'; state = 'exists'; timeout_ms = 1500 }
    if (-not $mouseDownWait.matched) { throw 'The test app did not observe the held left mouse button.' }
    Invoke-WcuTool -Name 'move_pointer' -Arguments @{ x = ($mouseX + 12); y = $mouseY; coordinate_space = 'screen'; duration_ms = 80 } | Out-Null
    $releasedMouse = Invoke-WcuTool -Name 'mouse_up' -Arguments @{ window_id = $windowId; x = ($mouseX + 12); y = $mouseY; coordinate_space = 'screen'; button = 'left' }
    if (@($releasedMouse.data.held_buttons).Count -ne 0 -or -not $releasedMouse.verification.verified) { throw 'mouse_up left a tracked mouse button held.' }
    $mouseUpWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; name = 'Mouse up: Left'; state = 'exists'; timeout_ms = 1500 }
    if (-not $mouseUpWait.matched) { throw 'The test app did not observe the left mouse button release.' }
    $buttonDrag = Invoke-WcuTool -Name 'drag' -Arguments @{ window_id = $windowId; from_x = $mouseX; from_y = $mouseY; to_x = ($mouseX + 20); to_y = $mouseY; coordinate_space = 'screen'; button = 'right'; duration_ms = 100 }
    if ($buttonDrag.data.button -ne 'right' -or @($buttonDrag.data.held_buttons).Count -ne 0) { throw 'The configurable-button drag did not finish with a clean mouse state.' }
    $rightDragWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; name = 'Mouse up: Right'; state = 'exists'; timeout_ms = 1500 }
    if (-not $rightDragWait.matched) { throw 'The test app did not observe the right-button drag release.' }
    $directMouseDown = Invoke-WcuTool -Name 'mouse_down' -Arguments @{ x = $mouseX; y = $mouseY; coordinate_space = 'screen'; button = 'middle' }
    if (@($directMouseDown.data.held_buttons) -notcontains 'middle' -or -not $directMouseDown.verification.verified) { throw 'Direct screen-space mouse_down did not hold the middle button.' }
    $directMouseDownWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; name = 'Mouse down: Middle'; state = 'exists'; timeout_ms = 1500 }
    if (-not $directMouseDownWait.matched) { throw 'The test app did not observe direct screen-space mouse_down.' }
    $directMouseUp = Invoke-WcuTool -Name 'mouse_up' -Arguments @{ x = $mouseX; y = $mouseY; coordinate_space = 'screen'; button = 'middle' }
    if (@($directMouseUp.data.held_buttons).Count -ne 0 -or -not $directMouseUp.verification.verified) { throw 'Direct screen-space mouse_up did not release the middle button.' }
    $directMouseUpWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; name = 'Mouse up: Middle'; state = 'exists'; timeout_ms = 1500 }
    if (-not $directMouseUpWait.matched) { throw 'The test app did not observe direct screen-space mouse_up.' }

    $script:stage = 'toggle-state'
    $toggle = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; automation_id = 'FeatureToggle'; limit = 2 }
    if ($toggle.count -ne 1 -or $toggle.controls[0].toggleState -ne 'Off') { throw "Initial UIA TogglePattern state was not exposed: $($toggle | ConvertTo-Json -Depth 6 -Compress)" }
    $toggleObservation = Invoke-WcuTool -Name 'inspect_window' -Arguments @{ window_id = $windowId; limit = 150 }
    $toggleAction = Invoke-WcuTool -Name 'perform_secondary_action' -Arguments @{ window_id = $windowId; control_id = $toggle.controls[0].id; action = 'toggle' }
    if (-not $toggleAction.ok -or -not $toggleAction.verification.verified) { throw 'Explicit UIA toggle action did not verify.' }
    $toggleAfter = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; automation_id = 'FeatureToggle'; limit = 2 }
    if ($toggleAfter.controls[0].toggleState -ne 'On') { throw 'UIA toggle state did not change to On.' }
    $toggleDiff = Invoke-WcuTool -Name 'observe_changes' -Arguments @{ window_id = $windowId; previous_observation_id = $toggleObservation.observationId; limit = 150 }
    $toggleChanges = @($toggleDiff.changes | Where-Object { $_.id -eq $toggle.controls[0].id -and $_.before.toggleState -eq 'Off' -and $_.after.toggleState -eq 'On' })
    if ($toggleChanges.Count -ne 1) { throw 'Incremental observation did not include the TogglePattern state transition.' }
    Invoke-WcuTool -Name 'perform_secondary_action' -Arguments @{ window_id = $windowId; control_id = $toggle.controls[0].id; action = 'toggle' } | Out-Null
    $delayedToggle = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; automation_id = 'DelayedToggleButton'; limit = 2 }
    if ($delayedToggle.count -ne 1) { throw 'Could not resolve the delayed state-transition control.' }
    Invoke-WcuTool -Name 'invoke' -Arguments @{ window_id = $windowId; control_id = $delayedToggle.controls[0].id } | Out-Null
    $toggleWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; control_id = $toggle.controls[0].id; state = 'toggle_on'; timeout_ms = 1500; poll_ms = 50 }
    if (-not $toggleWait.matched -or $toggleWait.elapsed_ms -lt 150 -or $toggleWait.elapsed_ms -gt 1500) { throw 'Toggle-state condition wait did not poll through the delayed UI transition within its deadline.' }

    $script:stage = 'selection-state'
    $beta = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; name = 'Beta'; control_type = 'ListItem'; limit = 2 }
    if ($beta.count -ne 1 -or $beta.controls[0].isSelected) { throw 'Initial UIA SelectionItem state was not exposed.' }
    Invoke-WcuTool -Name 'perform_secondary_action' -Arguments @{ window_id = $windowId; control_id = $beta.controls[0].id; action = 'select' } | Out-Null
    $selectionInspection = Invoke-WcuTool -Name 'inspect_window' -Arguments @{ window_id = $windowId; limit = 150 }
    $selectedBeta = @($selectionInspection.controls | Where-Object { $_.id -eq $beta.controls[0].id -and $_.isSelected })
    if ($selectedBeta.Count -ne 1 -or @($selectionInspection.selectedControlIds) -notcontains $beta.controls[0].id) {
        throw "Explicit UIA selection action or selected-control summary did not verify: beta=$($beta | ConvertTo-Json -Depth 6 -Compress) inspection=$($selectionInspection | ConvertTo-Json -Depth 6 -Compress)"
    }
    $selectionWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; control_id = $beta.controls[0].id; state = 'selected'; timeout_ms = 1500 }
    if (-not $selectionWait.matched) { throw 'Selection-state condition wait did not match the selected list item.' }

    $script:stage = 'semantic-invoke'
    $button = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; automation_id = 'CommitButton'; limit = 2 }
    if ($button.count -ne 1) {
        $button = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; name = 'Commit'; control_type = 'Button'; limit = 2 }
    }
    if ($button.count -ne 1) { throw 'Could not uniquely resolve the semantic button.' }
    $invoked = Invoke-WcuTool -Name 'invoke' -Arguments @{ window_id = $windowId; control_id = $button.controls[0].id }
    if (-not $invoked.ok) { throw 'Semantic invoke failed.' }
    $postInvokeWindows = Invoke-WcuTool -Name 'list_windows'
    if (@($postInvokeWindows.windows | Where-Object { $_.id -eq $windowId }).Count -ne 1) {
        $testApp.Refresh()
        throw "Target window disappeared after semantic invoke; process_exited=$($testApp.HasExited): $($postInvokeWindows | ConvertTo-Json -Depth 5 -Compress)"
    }

    $expected = 'Saved: ' + $testText
    $waited = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; name = $expected; state = 'exists'; timeout_ms = 5000 }
    if (-not $waited.matched) { throw 'UI condition did not become true after invoke.' }

    $snapshot = Invoke-WcuTool -Name 'snapshot' -Arguments @{ window_id = $windowId; limit = 100 }
    if (@($snapshot.content).Count -ne 2 -or $snapshot.content[1].type -ne 'image') { throw 'Snapshot did not return text and image content.' }
    $snapshotMeta = $snapshot.content[0].text | ConvertFrom-Json
    if (@($snapshotMeta.inspection.controls).Count -lt 4) { throw 'Snapshot UIA state is incomplete.' }
    $rootControl = @($snapshotMeta.inspection.controls | Where-Object { $_.depth -eq 0 })
    $nestedControls = @($snapshotMeta.inspection.controls | Where-Object { $_.depth -gt 0 -and $_.parentId })
    if ($rootControl.Count -ne 1 -or $rootControl[0].childCount -lt 1 -or $nestedControls.Count -lt 3) { throw 'Hierarchical UIA relationships are incomplete.' }
    if ($snapshotMeta.capture.sha256 -notmatch '^[0-9a-f]{64}$' -or -not $snapshotMeta.capture.capturedAt) { throw 'Snapshot freshness metadata is incomplete.' }
    if ($RequireWgc -and $snapshotMeta.capture.backend -ne 'windows-graphics-capture') { throw 'Snapshot did not use Windows Graphics Capture.' }

    $changedText = 'Changed ' + [char]0x72B6 + [char]0x6001
    Invoke-WcuTool -Name 'enter_text' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; text = $changedText } | Out-Null
    Invoke-WcuTool -Name 'invoke' -Arguments @{ window_id = $windowId; control_id = $button.controls[0].id } | Out-Null
    $changedExpected = 'Saved: ' + $changedText
    $changedWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; name = $changedExpected; state = 'exists'; timeout_ms = 5000 }
    if (-not $changedWait.matched) { throw 'Second semantic state did not appear.' }
    $diff = Invoke-WcuTool -Name 'observe_changes' -Arguments @{ window_id = $windowId; previous_observation_id = $snapshotMeta.inspection.observationId; limit = 100 }
    if (@($diff.changes).Count -lt 1 -or -not $diff.observationId) { throw 'Incremental UIA observation did not report the semantic state change.' }

    $semanticInvalidatedScreenshot = $false
    try {
        Invoke-WcuTool -Name 'click' -Arguments @{ window_id = $windowId; x = 20; y = 20; screenshot_id = $snapshotMeta.capture.id } | Out-Null
    } catch {
        if ($_.Exception.Message -match 'Unknown or expired') { $semanticInvalidatedScreenshot = $true } else { throw }
    }
    if (-not $semanticInvalidatedScreenshot) { throw 'A semantic mutation did not invalidate the prior screenshot.' }

    $script:stage = 'pixel-grounding'
    $pixelText = 'Pixel mapped'
    Invoke-WcuTool -Name 'enter_text' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; text = $pixelText } | Out-Null
    $pixelSnapshot = Invoke-WcuTool -Name 'snapshot' -Arguments @{ window_id = $windowId; limit = 100 }
    $pixelSnapshotMeta = $pixelSnapshot.content[0].text | ConvertFrom-Json
    if ($pixelSnapshotMeta.capture.bounds.x -ne $pixelSnapshotMeta.inspection.window.visibleBounds.x -or $pixelSnapshotMeta.capture.bounds.y -ne $pixelSnapshotMeta.inspection.window.visibleBounds.y) {
        throw 'WGC image origin does not match the DWM visible frame origin.'
    }
    $ocrTarget = Invoke-WcuTool -Name 'find_text' -Arguments @{ window_id = $windowId; text = 'SAVE'; match = 'exact'; limit = 10 }
    $ocrMatches = @($ocrTarget.matches | Where-Object { $_.kind -eq 'word' })
    if ($ocrTarget.count -lt 1 -or $ocrMatches.Count -lt 1 -or -not $ocrTarget.screenshot_id) {
        throw "OCR text grounding did not resolve the target button: $($ocrTarget | ConvertTo-Json -Depth 8 -Compress)"
    }
    $pixelX = [int]$ocrMatches[0].center.x
    $pixelY = [int]$ocrMatches[0].center.y

    $screenMoveX = [int]$target[0].visibleBounds.x + 8
    $screenMoveY = [int]$target[0].visibleBounds.y + 8
    Invoke-WcuTool -Name 'move_pointer' -Arguments @{ x = $screenMoveX; y = $screenMoveY; coordinate_space = 'screen' } | Out-Null
    $screenPointer = Invoke-WcuTool -Name 'pointer_position'
    if ($screenPointer.x -ne $screenMoveX -or $screenPointer.y -ne $screenMoveY) { throw 'Screen-space pointer movement did not verify.' }
    Invoke-WcuTool -Name 'move_pointer' -Arguments @{ window_id = $windowId; x = 12; y = 12; coordinate_space = 'window' } | Out-Null
    $windowPointer = Invoke-WcuTool -Name 'pointer_position'
    if ($windowPointer.x -ne ([int]$target[0].bounds.x + 12) -or $windowPointer.y -ne ([int]$target[0].bounds.y + 12)) { throw 'Window-space pointer movement did not verify.' }
    Invoke-WcuTool -Name 'move_pointer' -Arguments @{ x = $pixelX; y = $pixelY; coordinate_space = 'screenshot'; screenshot_id = $ocrTarget.screenshot_id; duration_ms = 120 } | Out-Null
    $screenshotPointer = Invoke-WcuTool -Name 'pointer_position'
    $expectedPointerX = [int]$ocrTarget.capture_bounds.x + $pixelX
    $expectedPointerY = [int]$ocrTarget.capture_bounds.y + $pixelY
    if ($screenshotPointer.x -ne $expectedPointerX -or $screenshotPointer.y -ne $expectedPointerY) { throw 'Screenshot-space pointer movement did not verify.' }

    $staleRejected = $false
    Start-Sleep -Milliseconds 120
    try {
        Invoke-WcuTool -Name 'click' -Arguments @{ window_id = $windowId; x = $pixelX; y = $pixelY; coordinate_space = 'screenshot'; screenshot_id = $ocrTarget.screenshot_id; max_age_ms = 100 } | Out-Null
    } catch {
        if ($_.Exception.Message -match 'stale') { $staleRejected = $true } else { throw }
    }
    if (-not $staleRejected) { throw 'Stale screenshot coordinates were not rejected.' }

    $pixelClick = Invoke-WcuTool -Name 'click' -Arguments @{ window_id = $windowId; x = $pixelX; y = $pixelY; coordinate_space = 'screenshot'; screenshot_id = $ocrTarget.screenshot_id }
    if (-not $pixelClick.ok -or $pixelClick.verification.strategy -ne 'window-and-screenshot-reobserve' -or -not $pixelClick.data.after_screenshot_id) {
        throw 'Screenshot-bound pixel action did not re-observe the window.'
    }
    $pixelExpected = 'Saved: ' + $pixelText
    $pixelWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; name = $pixelExpected; state = 'exists'; timeout_ms = 5000 }
    if (-not $pixelWait.matched) { throw 'Screenshot-space coordinate mapping did not invoke the target button.' }

    $script:stage = 'desktop-grounding'
    $desktopText = 'Desktop mapped'
    Invoke-WcuTool -Name 'enter_text' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; text = $desktopText } | Out-Null
    $desktopCapture = Invoke-WcuTool -Name 'capture' -Arguments @{ desktop = $true }
    $desktopMeta = $desktopCapture.content[0].text | ConvertFrom-Json
    if (-not $desktopMeta.id -or $desktopMeta.bounds.x -ne $displayInfo.virtualDesktop.x -or $desktopMeta.bounds.y -ne $displayInfo.virtualDesktop.y -or $desktopMeta.bounds.width -ne $displayInfo.virtualDesktop.width -or $desktopMeta.bounds.height -ne $displayInfo.virtualDesktop.height) {
        throw 'Virtual-desktop capture metadata does not match the physical display topology.'
    }
    $desktopSelectorConflictRejected = $false
    try {
        Invoke-WcuTool -Name 'move_pointer' -Arguments @{ window_id = $windowId; x = 0; y = 0; coordinate_space = 'screenshot'; screenshot_id = $desktopMeta.id } | Out-Null
    } catch {
        if ($_.Exception.Message -match 'window selector') { $desktopSelectorConflictRejected = $true } else { throw }
    }
    if (-not $desktopSelectorConflictRejected) { throw 'Virtual-desktop screenshot accepted a conflicting window selector.' }
    Start-Sleep -Milliseconds 120
    $staleDesktopRejected = $false
    try {
        Invoke-WcuTool -Name 'move_pointer' -Arguments @{ x = 0; y = 0; coordinate_space = 'screenshot'; screenshot_id = $desktopMeta.id; max_age_ms = 100 } | Out-Null
    } catch {
        if ($_.Exception.Message -match 'stale') { $staleDesktopRejected = $true } else { throw }
    }
    if (-not $staleDesktopRejected) { throw 'Stale virtual-desktop screenshot coordinates were not rejected.' }
    $desktopOcr = Invoke-WcuTool -Name 'find_text' -Arguments @{ desktop = $true; text = 'SAVE'; match = 'exact'; limit = 20 }
    $desktopOcrMatches = @($desktopOcr.matches | Where-Object {
        $_.kind -eq 'word' -and
        $_.screen_bounds.x -lt $button.controls[0].bounds.right -and $_.screen_bounds.right -gt $button.controls[0].bounds.x -and
        $_.screen_bounds.y -lt $button.controls[0].bounds.bottom -and $_.screen_bounds.bottom -gt $button.controls[0].bounds.y
    })
    if (-not $desktopOcr.ok -or -not $desktopOcr.screenshot_id -or $desktopOcr.coordinate_space -ne 'screenshot' -or $desktopOcr.capture_bounds.x -ne $displayInfo.virtualDesktop.x -or $desktopOcr.capture_bounds.y -ne $displayInfo.virtualDesktop.y -or $desktopOcrMatches.Count -lt 1) {
        throw 'Fresh virtual-desktop OCR grounding did not return an actionable match for the visible button.'
    }
    $desktopButtonX = [int]$desktopOcrMatches[0].center.x
    $desktopButtonY = [int]$desktopOcrMatches[0].center.y
    Invoke-WcuTool -Name 'move_pointer' -Arguments @{ x = $desktopButtonX; y = $desktopButtonY; coordinate_space = 'screenshot'; screenshot_id = $desktopOcr.screenshot_id } | Out-Null
    $desktopPointer = Invoke-WcuTool -Name 'pointer_position'
    if ($desktopPointer.x -ne ([int]$desktopOcr.capture_bounds.x + $desktopButtonX) -or $desktopPointer.y -ne ([int]$desktopOcr.capture_bounds.y + $desktopButtonY)) {
        throw 'Virtual-desktop screenshot coordinates did not map to physical pointer coordinates.'
    }
    $desktopClick = Invoke-WcuTool -Name 'click' -Arguments @{ x = $desktopButtonX; y = $desktopButtonY; coordinate_space = 'screenshot'; screenshot_id = $desktopOcr.screenshot_id }
    if (-not $desktopClick.ok -or $desktopClick.verification.strategy -ne 'desktop-screenshot-reobserve' -or -not $desktopClick.data.after_screenshot_id) {
        throw 'Virtual-desktop screenshot-bound click did not re-observe the whole desktop.'
    }
    $desktopExpected = 'Saved: ' + $desktopText
    $desktopWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; name = $desktopExpected; state = 'exists'; timeout_ms = 5000 }
    if (-not $desktopWait.matched) { throw 'Virtual-desktop screenshot-bound click did not invoke the visible target.' }
    $desktopMouseX = $mouseX - [int]$desktopMeta.bounds.x
    $desktopMouseY = $mouseY - [int]$desktopMeta.bounds.y
    $desktopDrag = Invoke-WcuTool -Name 'drag' -Arguments @{ from_x = $desktopMouseX; from_y = $desktopMouseY; to_x = ($desktopMouseX + 16); to_y = $desktopMouseY; coordinate_space = 'screenshot'; screenshot_id = $desktopClick.data.after_screenshot_id; button = 'middle'; duration_ms = 80 }
    if (-not $desktopDrag.ok -or $desktopDrag.verification.strategy -ne 'desktop-screenshot-reobserve' -or @($desktopDrag.data.held_buttons).Count -ne 0 -or -not $desktopDrag.data.after_screenshot_id) {
        throw 'Virtual-desktop screenshot-bound drag did not finish and re-observe safely.'
    }
    $desktopDragPointer = Invoke-WcuTool -Name 'pointer_position'
    if ($desktopDragPointer.x -ne ($mouseX + 16) -or $desktopDragPointer.y -ne $mouseY) { throw 'Virtual-desktop screenshot-bound drag ended at the wrong physical point.' }

    $script:stage = 'image-grounding'
    $imageText = 'Image matched'
    Invoke-WcuTool -Name 'enter_text' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; text = $imageText } | Out-Null
    $currentButton = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; automation_id = 'CommitButton'; limit = 2 }
    if ($currentButton.count -ne 1) { throw 'Could not resolve the visual template source control.' }
    $visualCapture = Invoke-WcuTool -Name 'capture' -Arguments @{ window_id = $windowId; path = $visualSourcePath }
    $visualMeta = $visualCapture.content[0].text | ConvertFrom-Json
    $cropX = [int]$currentButton.controls[0].bounds.x - [int]$visualMeta.bounds.x
    $cropY = [int]$currentButton.controls[0].bounds.y - [int]$visualMeta.bounds.y
    $cropWidth = [int]$currentButton.controls[0].bounds.width
    $cropHeight = [int]$currentButton.controls[0].bounds.height
    if ($cropX -lt 0 -or $cropY -lt 0 -or $cropWidth -lt 2 -or $cropHeight -lt 2 -or $cropX + $cropWidth -gt $visualMeta.width -or $cropY + $cropHeight -gt $visualMeta.height) {
        throw 'UIA button bounds could not be mapped into the visual source capture.'
    }
    Add-Type -AssemblyName System.Drawing
    $sourceBitmap = $null
    $templateBitmap = $null
    $scaledTemplateBitmap = $null
    $scaledTemplateGraphics = $null
    try {
        $sourceBitmap = [System.Drawing.Bitmap]::FromFile($visualSourcePath)
        $cropRectangle = [System.Drawing.Rectangle]::new($cropX, $cropY, $cropWidth, $cropHeight)
        $templateBitmap = $sourceBitmap.Clone($cropRectangle, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $templateBitmap.Save($templatePath, [System.Drawing.Imaging.ImageFormat]::Png)
        $scaledWidth = [Math]::Max(2, [int][Math]::Round($cropWidth * 0.8))
        $scaledHeight = [Math]::Max(2, [int][Math]::Round($cropHeight * 0.8))
        $scaledTemplateBitmap = [System.Drawing.Bitmap]::new($scaledWidth, $scaledHeight, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $scaledTemplateGraphics = [System.Drawing.Graphics]::FromImage($scaledTemplateBitmap)
        $scaledTemplateGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $scaledTemplateGraphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $scaledTemplateGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $scaledTemplateGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scaledTemplateGraphics.DrawImage($templateBitmap, [System.Drawing.Rectangle]::new(0, 0, $scaledWidth, $scaledHeight), 0, 0, $templateBitmap.Width, $templateBitmap.Height, [System.Drawing.GraphicsUnit]::Pixel)
        $scaledTemplateBitmap.Save($scaledTemplatePath, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        if ($null -ne $scaledTemplateGraphics) { $scaledTemplateGraphics.Dispose() }
        if ($null -ne $scaledTemplateBitmap) { $scaledTemplateBitmap.Dispose() }
        if ($null -ne $templateBitmap) { $templateBitmap.Dispose() }
        if ($null -ne $sourceBitmap) { $sourceBitmap.Dispose() }
    }
    $imageTarget = Invoke-WcuTool -Name 'find_image' -Arguments @{ window_id = $windowId; template_path = $templatePath; threshold = 0.97; max_results = 5 }
    $imageMatches = @($imageTarget.matches | Where-Object {
        $_.screen_bounds.x -lt $currentButton.controls[0].bounds.right -and $_.screen_bounds.right -gt $currentButton.controls[0].bounds.x -and
        $_.screen_bounds.y -lt $currentButton.controls[0].bounds.bottom -and $_.screen_bounds.bottom -gt $currentButton.controls[0].bounds.y
    })
    if ($imageTarget.count -lt 1 -or $imageMatches.Count -lt 1 -or $imageMatches[0].score -lt 0.97 -or $imageTarget.elapsed_ms -gt 2000 -or -not $imageTarget.screenshot_id) {
        throw "Local image template grounding did not resolve the button: $($imageTarget | ConvertTo-Json -Depth 8 -Compress)"
    }
    $scaledImageTarget = Invoke-WcuTool -Name 'find_image' -Arguments @{ window_id = $windowId; template_path = $scaledTemplatePath; threshold = 0.90; max_results = 5; scale_min = 1.15; scale_max = 1.35; scale_step = 0.025 }
    $scaledImageMatches = @($scaledImageTarget.matches | Where-Object {
        $_.screen_bounds.x -lt $currentButton.controls[0].bounds.right -and $_.screen_bounds.right -gt $currentButton.controls[0].bounds.x -and
        $_.screen_bounds.y -lt $currentButton.controls[0].bounds.bottom -and $_.screen_bounds.bottom -gt $currentButton.controls[0].bounds.y
    })
    if ($scaledImageTarget.backend -ne 'local-template-multiscale-sampled-sad' -or $scaledImageTarget.count -lt 1 -or $scaledImageMatches.Count -lt 1 -or $scaledImageMatches[0].score -lt 0.90 -or $scaledImageMatches[0].scale -lt 1.15 -or $scaledImageMatches[0].scale -gt 1.35 -or $scaledImageTarget.elapsed_ms -gt 5000) {
        throw "Multi-scale image grounding did not recover the resized real-window template: $($scaledImageTarget | ConvertTo-Json -Depth 8 -Compress)"
    }
    $imageClick = Invoke-WcuTool -Name 'click' -Arguments @{ window_id = $windowId; x = [int]$scaledImageMatches[0].center.x; y = [int]$scaledImageMatches[0].center.y; coordinate_space = 'screenshot'; screenshot_id = $scaledImageTarget.screenshot_id }
    if (-not $imageClick.ok) { throw 'Image-template screenshot-bound click failed.' }
    $imageExpected = 'Saved: ' + $imageText
    $imageWait = Invoke-WcuTool -Name 'wait_for_ui' -Arguments @{ window_id = $windowId; name = $imageExpected; state = 'exists'; timeout_ms = 5000 }
    if (-not $imageWait.matched) { throw 'Image-template coordinate mapping did not invoke the target button.' }

    $occluder = Start-Process -FilePath $testAppPath -ArgumentList '--occluder' -PassThru
    $occluderDeadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 100
        $occluder.Refresh()
    } until ($occluder.MainWindowHandle -ne 0 -or [DateTime]::UtcNow -ge $occluderDeadline)
    if ($occluder.MainWindowHandle -eq 0) { throw 'Occluder did not create a visible window.' }

    $captured = Invoke-WcuTool -Name 'capture' -Arguments @{ window_id = $windowId; path = $capturePath }
    if (-not (Test-Path -LiteralPath $capturePath)) { throw 'Capture file was not created.' }
    $captureMeta = $captured.content[0].text | ConvertFrom-Json
    if ($captureMeta.width -lt 100 -or $captureMeta.height -lt 100) { throw 'Capture dimensions are invalid.' }
    if ($RequireWgc -and $captureMeta.backend -ne 'windows-graphics-capture') {
        throw "Expected Windows Graphics Capture, got $($captureMeta.backend)."
    }

    $ocr = Invoke-WcuTool -Name 'ocr' -Arguments @{ path = $capturePath }
    if (-not $ocr.ok -or $ocr.text -notmatch 'Semantic UI automation test') { throw 'Windows OCR did not recognize the test window heading.' }
    $freshOcr = Invoke-WcuTool -Name 'ocr' -Arguments @{ window_id = $windowId }
    if (-not $freshOcr.ok -or -not $freshOcr.screenshot_id -or $freshOcr.coordinate_space -ne 'screenshot') { throw 'Fresh-window OCR did not return screenshot-bound coordinate metadata.' }

    $dialogLauncher = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; automation_id = 'DialogButton'; limit = 2 }
    if ($dialogLauncher.count -ne 1) { throw 'Could not uniquely resolve the transient-dialog launcher.' }
    Invoke-WcuTool -Name 'invoke' -Arguments @{ window_id = $windowId; control_id = $dialogLauncher.controls[0].id } | Out-Null
    $dialogWait = Invoke-WcuTool -Name 'wait_for_window' -Arguments @{ title = 'Windows Computer Use Dialog'; owner_window_id = $windowId; state = 'exists'; timeout_ms = 5000 }
    if (-not $dialogWait.matched -or $dialogWait.count -ne 1 -or $dialogWait.windows[0].ownerWindowId -ne $windowId -or $dialogWait.windows[0].rootOwnerWindowId -ne $windowId) {
        throw "Owned transient dialog was not linked to its main window: $($dialogWait | ConvertTo-Json -Depth 8 -Compress)"
    }
    $dialogId = [long]$dialogWait.windows[0].id
    $dialogClose = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $dialogId; automation_id = 'DialogCloseButton'; limit = 2 }
    if ($dialogClose.count -ne 1) { throw 'Could not uniquely resolve the transient-dialog close button.' }
    Invoke-WcuTool -Name 'invoke' -Arguments @{ window_id = $dialogId; control_id = $dialogClose.controls[0].id } | Out-Null
    $dialogClosed = Invoke-WcuTool -Name 'wait_for_window' -Arguments @{ title = 'Windows Computer Use Dialog'; owner_window_id = $windowId; state = 'absent'; timeout_ms = 5000 }
    if (-not $dialogClosed.matched) { throw 'Owned transient dialog did not disappear.' }

    $minimized = Invoke-WcuTool -Name 'set_window_state' -Arguments @{ window_id = $windowId; state = 'minimize' }
    if (-not $minimized.ok -or -not $minimized.window.isMinimized) { throw 'Window did not reach the minimized state.' }
    $minimizedCaptureRejected = $false
    try {
        Invoke-WcuTool -Name 'capture' -Arguments @{ window_id = $windowId } | Out-Null
    } catch {
        if ($_.Exception.Message -match 'minimized.*Restore') { $minimizedCaptureRejected = $true } else { throw }
    }
    if (-not $minimizedCaptureRejected) { throw 'Minimized capture was not rejected with explicit recovery guidance.' }
    $restored = Invoke-WcuTool -Name 'set_window_state' -Arguments @{ window_id = $windowId; state = 'restore' }
    if (-not $restored.ok -or $restored.window.isMinimized -or $restored.window.isMaximized) { throw 'Window did not return to the restored state.' }

    $script:stage = 'window-rehydration'
    $recreateButton = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; automation_id = 'RecreateWindowButton'; limit = 2 }
    if ($recreateButton.count -ne 1) { throw 'Could not resolve the deterministic HWND recreation control.' }
    Invoke-WcuTool -Name 'invoke' -Arguments @{ window_id = $windowId; control_id = $recreateButton.controls[0].id } | Out-Null
    $rehydratedInspection = Invoke-WcuTool -Name 'inspect_window' -Arguments @{ window_id = $windowId; limit = 150 }
    if ($rehydratedInspection.window.processId -ne $target[0].processId -or @($rehydratedInspection.controls).Count -lt 5) {
        throw 'The original window id did not recover the recreated same-process HWND.'
    }
    $windowHandleChanged = [long]$rehydratedInspection.window.id -ne $windowId
    $heldForCleanup = Invoke-WcuTool -Name 'key_down' -Arguments @{ window_id = $windowId; key = 'ctrl' }
    if (@($heldForCleanup.data.held_keys) -notcontains 'ctrl') { throw 'Could not stage a held key for end_session cleanup.' }
    $cleanupSurface = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; automation_id = 'MouseSurface'; limit = 2 }
    if ($cleanupSurface.count -ne 1) { throw 'Could not re-resolve the mouse surface after HWND recreation.' }
    $cleanupMouseX = [int]$cleanupSurface.controls[0].bounds.x + 8
    $cleanupMouseY = [int]$cleanupSurface.controls[0].bounds.y + 8
    $heldMouseForCleanup = Invoke-WcuTool -Name 'mouse_down' -Arguments @{ window_id = $windowId; x = $cleanupMouseX; y = $cleanupMouseY; coordinate_space = 'screen'; button = 'right' }
    if (@($heldMouseForCleanup.data.held_buttons) -notcontains 'right') { throw 'Could not stage a held mouse button for end_session cleanup.' }
    $ended = Invoke-WcuTool -Name 'end_session'
    if (-not $ended.ok -or $ended.released_keys -ne 1 -or $ended.released_buttons -ne 1 -or $ended.discarded_clipboard_backups -ne 0) { throw 'Session did not release held input or clear clipboard backup state cleanly.' }

    [ordered]@{
        ok = $true
        protocol = $initialize.protocolVersion
        tools = @($tools.tools).Count
        displays = @($displayInfo.displays).Count
        atomic_desktop_observation = $true
        desktop_observation_windows = @($desktopObservationMeta.windows).Count
        desktop_observation_screenshot_action = $observationMove.ok
        primary_scale_percent = $displayInfo.displays[0].scalePercent
        virtual_desktop = "$($displayInfo.virtualDesktop.x),$($displayInfo.virtualDesktop.y),$($displayInfo.virtualDesktop.width),$($displayInfo.virtualDesktop.height)"
        window_id = $windowId
        window_bounds_roundtrip = $true
        controls = @($inspection.controls).Count
        text_backend = $entered.backend
        invoke_backend = $invoked.backend
        wait_matched = $waited.matched
        capture_backend = $captureMeta.backend
        snapshot_backend = $snapshotMeta.capture.backend
        hierarchical_controls = $nestedControls.Count
        incremental_changes = @($diff.changes).Count
        semantic_screenshot_invalidation = $semanticInvalidatedScreenshot
        screenshot_bound_action = $pixelClick.verification.strategy
        screenshot_space_mapping = $pixelWait.matched
        desktop_screenshot_bound_action = $desktopClick.verification.strategy
        desktop_screenshot_space_mapping = $desktopWait.matched
        desktop_screenshot_bound_drag = $desktopDrag.verification.strategy
        desktop_stale_screenshot_rejected = $staleDesktopRejected
        desktop_selector_conflict_rejected = $desktopSelectorConflictRejected
        desktop_ocr_screenshot_bound = $desktopOcrMatches.Count
        ocr_text_grounding = $ocrTarget.count
        stale_screenshot_rejected = $staleRejected
        occluded_window_capture = $true
        capture_verified = $true
        capture_path = if ($KeepArtifacts) { $capturePath } else { $null }
        ocr_ok = [bool]$ocr.ok
        ocr_backend = $ocr.backend
        ocr_text_length = if ($null -ne $ocr.text) { $ocr.text.Length } else { 0 }
        fresh_ocr_screenshot_bound = [bool]$freshOcr.screenshot_id
        owned_dialog_linked = $dialogWait.matched
        transient_dialog_closed = $dialogClosed.matched
        minimized_capture_rejected = $minimizedCaptureRejected
        window_state_restored = -not $restored.window.isMinimized
        pointer_coordinate_spaces_verified = 3
        value_state_exposed = $inputState.controls[0].value -eq $testText
        selected_text_exposed = $textSelectionState.selectedText -eq $testText
        toggle_state_diff = $toggleChanges.Count
        selection_state_exposed = $selectedBeta.Count
        value_condition_wait = $valueWait.matched
        value_condition_wait_ms = $valueWait.elapsed_ms
        delayed_toggle_wait_ms = $toggleWait.elapsed_ms
        selection_condition_wait = $selectionWait.matched
        selection_condition_wait_ms = $selectionWait.elapsed_ms
        image_template_grounding = $imageTarget.count
        image_match_score = $imageMatches[0].score
        image_match_ms = $imageTarget.elapsed_ms
        multiscale_image_match = $scaledImageMatches[0].scale
        multiscale_image_match_ms = $scaledImageTarget.elapsed_ms
        image_screenshot_space_mapping = $imageWait.matched
        held_key_roundtrip = $shiftDownWait.matched -and $shiftUpWait.matched
        implied_shift_repeat = $uppercaseWait.matched
        desktop_keyboard_text = $desktopTextWait.matched
        desktop_keyboard_hold = $desktopShiftDownWait.matched -and $desktopShiftUpWait.matched
        desktop_keyboard_conflict_rejected = $desktopConflictRejected
        held_mouse_roundtrip = $mouseDownWait.matched -and $mouseUpWait.matched
        configurable_button_drag = $rightDragWait.matched
        direct_screen_mouse_roundtrip = $directMouseDownWait.matched -and $directMouseUpWait.matched
        clipboard_roundtrip = $clipboardRoundtrip
        atomic_paste_roundtrip = $atomicPasteRoundtrip
        atomic_paste_append = $atomicPasteAppend
        atomic_paste_failure_restore = $atomicPasteFailureRestore
        atomic_copy_all = $atomicCopyAll
        atomic_copy_current = $atomicCopyCurrent
        atomic_copy_failure_restore = $atomicCopyFailureRestore
        end_session_released_keys = $ended.released_keys
        end_session_released_buttons = $ended.released_buttons
        end_session_discarded_clipboard_backups = $ended.discarded_clipboard_backups
        recreated_window_recovered = $true
        window_handle_changed = $windowHandleChanged
    } | ConvertTo-Json -Depth 6
} finally {
    if ($clipboardBackupId -and $null -ne $mcp -and -not $mcp.HasExited) {
        try {
            Invoke-WcuTool -Name 'restore_clipboard' -Arguments @{ backup_id = $clipboardBackupId } | Out-Null
            $clipboardBackupId = $null
        } catch {
            Write-Warning 'The E2E fallback could not restore the clipboard backup before MCP shutdown.'
        }
    }
    if ($null -ne $mcp) {
        try { $mcp.StandardInput.Close() } catch {}
        if (-not $mcp.WaitForExit(2000)) { try { $mcp.Kill() } catch {} }
        if ($null -ne $mcpErrorTask) { try { $mcpErrorTask.GetAwaiter().GetResult() | Out-Null } catch {} }
        $mcp.Dispose()
    }
    if (-not $KeepTestWindow -and $null -ne $testApp) {
        try { $testApp.CloseMainWindow() | Out-Null } catch {}
        if (-not $testApp.WaitForExit(1500)) { try { $testApp.Kill() } catch {} }
        $testApp.Dispose()
    }
    if (-not $KeepTestWindow -and $null -ne $occluder) {
        try { $occluder.CloseMainWindow() | Out-Null } catch {}
        if (-not $occluder.WaitForExit(1500)) { try { $occluder.Kill() } catch {} }
        $occluder.Dispose()
    }
    $ownedArtifactPaths = @($capturePath, $visualSourcePath, $templatePath, $scaledTemplatePath)
    if (-not $KeepArtifacts) {
        foreach ($ownedArtifactPath in $ownedArtifactPaths) {
            if (Test-Path -LiteralPath $ownedArtifactPath) { Remove-Item -LiteralPath $ownedArtifactPath -Force }
        }
        $remainingOwnedArtifacts = @($ownedArtifactPaths | Where-Object { Test-Path -LiteralPath $_ })
        if ($remainingOwnedArtifacts.Count -ne 0) {
            throw "E2E cleanup left owned artifacts: $($remainingOwnedArtifacts -join ', ')"
        }
    }
}
