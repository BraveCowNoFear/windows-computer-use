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
$capturePath = Join-Path $env:TEMP ("windows-computer-use-e2e-{0}.png" -f [guid]::NewGuid().ToString('N'))
$nextId = 0

function Invoke-McpRequest {
    param([string]$Method, [hashtable]$Params = @{})
    $script:nextId++
    $payload = [ordered]@{ jsonrpc = '2.0'; id = $script:nextId; method = $Method; params = $Params }
    $script:mcp.StandardInput.WriteLine(($payload | ConvertTo-Json -Depth 20 -Compress))
    $script:mcp.StandardInput.Flush()
    $line = $script:mcp.StandardOutput.ReadLine()
    if ($null -eq $line) {
        $stderr = $script:mcp.StandardError.ReadToEnd()
        throw "MCP process closed before replying. $stderr"
    }
    $response = $line | ConvertFrom-Json
    if ($null -ne $response.error) { throw "MCP error: $($response.error.message)" }
    return $response.result
}

function Invoke-WcuTool {
    param([string]$Name, [hashtable]$Arguments = @{})
    $result = Invoke-McpRequest -Method 'tools/call' -Params @{ name = $Name; arguments = $Arguments }
    if ($result.isError) { throw "Tool $Name failed: $($result.content[0].text)" }
    if ($Name -in @('capture', 'snapshot')) { return $result }
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

    $initialize = Invoke-McpRequest -Method 'initialize' -Params @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'e2e-test'; version = '1.0' } }
    if ($initialize.serverInfo.name -ne 'windows-computer-use') { throw 'Unexpected MCP server identity.' }
    $tools = Invoke-McpRequest -Method 'tools/list'
    if (@($tools.tools).Count -lt 18) { throw 'MCP tool catalog is incomplete.' }

    $windows = Invoke-WcuTool -Name 'list_windows'
    $target = @($windows.windows | Where-Object { $_.title -eq 'Windows Computer Use Test App' })
    if ($target.Count -ne 1) { throw "Expected one test window, found $($target.Count)." }
    $windowId = [long]$target[0].id

    $inspection = Invoke-WcuTool -Name 'inspect_window' -Arguments @{ window_id = $windowId; limit = 100 }
    if (@($inspection.controls).Count -lt 4) { throw 'UIA inspection returned too few controls.' }

    $input = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; automation_id = 'InputBox'; limit = 2 }
    if ($input.count -ne 1) {
        $input = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; name = 'Input'; control_type = 'Edit'; limit = 2 }
    }
    if ($input.count -ne 1) { throw 'Could not uniquely resolve the semantic input control.' }

    $testText = 'Codex ' + [char]0x4F60 + [char]0x597D
    $entered = Invoke-WcuTool -Name 'enter_text' -Arguments @{ window_id = $windowId; control_id = $input.controls[0].id; text = $testText }
    if (-not $entered.ok -or -not $entered.verification.verified) { throw 'Text entry did not verify.' }

    $button = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; automation_id = 'CommitButton'; limit = 2 }
    if ($button.count -ne 1) {
        $button = Invoke-WcuTool -Name 'find_controls' -Arguments @{ window_id = $windowId; name = 'Commit'; control_type = 'Button'; limit = 2 }
    }
    if ($button.count -ne 1) { throw 'Could not uniquely resolve the semantic button.' }
    $invoked = Invoke-WcuTool -Name 'invoke' -Arguments @{ window_id = $windowId; control_id = $button.controls[0].id }
    if (-not $invoked.ok) { throw 'Semantic invoke failed.' }

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

    $pixelSnapshot = Invoke-WcuTool -Name 'snapshot' -Arguments @{ window_id = $windowId; limit = 100 }
    $pixelSnapshotMeta = $pixelSnapshot.content[0].text | ConvertFrom-Json

    $staleRejected = $false
    Start-Sleep -Milliseconds 120
    try {
        Invoke-WcuTool -Name 'click' -Arguments @{ window_id = $windowId; x = 20; y = 20; screenshot_id = $pixelSnapshotMeta.capture.id; max_age_ms = 100 } | Out-Null
    } catch {
        if ($_.Exception.Message -match 'stale') { $staleRejected = $true } else { throw }
    }
    if (-not $staleRejected) { throw 'Stale screenshot coordinates were not rejected.' }

    $pixelClick = Invoke-WcuTool -Name 'click' -Arguments @{ window_id = $windowId; x = 20; y = 20; screenshot_id = $pixelSnapshotMeta.capture.id }
    if (-not $pixelClick.ok -or $pixelClick.verification.strategy -ne 'window-and-screenshot-reobserve' -or -not $pixelClick.data.after_screenshot_id) {
        throw 'Screenshot-bound pixel action did not re-observe the window.'
    }

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
    $ended = Invoke-WcuTool -Name 'end_session'
    if (-not $ended.ok) { throw 'Session did not end cleanly.' }

    [ordered]@{
        ok = $true
        protocol = $initialize.protocolVersion
        tools = @($tools.tools).Count
        window_id = $windowId
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
        stale_screenshot_rejected = $staleRejected
        occluded_window_capture = $true
        capture_verified = $true
        capture_path = if ($KeepArtifacts) { $capturePath } else { $null }
        ocr_ok = [bool]$ocr.ok
        ocr_backend = $ocr.backend
        ocr_text_length = if ($null -ne $ocr.text) { $ocr.text.Length } else { 0 }
    } | ConvertTo-Json -Depth 6
} finally {
    if ($null -ne $mcp) {
        try { $mcp.StandardInput.Close() } catch {}
        if (-not $mcp.WaitForExit(2000)) { try { $mcp.Kill() } catch {} }
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
    if (-not $KeepArtifacts -and (Test-Path -LiteralPath $capturePath)) {
        Remove-Item -LiteralPath $capturePath -Force
    }
}
