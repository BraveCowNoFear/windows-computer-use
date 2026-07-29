$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$mcpPath = Join-Path $pluginRoot 'dist\win-x64\mcp\WindowsComputerUse.Mcp.exe'
$brokerPath = Join-Path $pluginRoot 'dist\win-x64\broker\WindowsComputerUse.Broker.exe'
$testAppPath = Join-Path $pluginRoot 'src\WindowsComputerUse.TestApp\bin\Release\net8.0-windows7.0\WindowsComputerUse.TestApp.exe'
foreach ($requiredPath in @($mcpPath, $brokerPath, $testAppPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) { throw "Missing build output: $requiredPath. Run scripts/build.ps1 first." }
}

$mcp = $null
$stderrTask = $null
$testApp = $null
$nextId = 0

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WcuRecoveryInputProbe {
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    public static bool IsDown(int virtualKey) { return (GetAsyncKeyState(virtualKey) & 0x8000) != 0; }
    public static void ReleaseOwnedInput() {
        keybd_event(0x11, 0, 0x0002, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
}
'@

function Get-ChildBrokerProcess {
    param([Parameter(Mandatory)][int]$ParentProcessId)
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $candidate = Get-CimInstance Win32_Process -Filter "ParentProcessId = $ParentProcessId" -ErrorAction Stop |
            Where-Object { $_.Name -eq 'WindowsComputerUse.Broker.exe' } |
            Select-Object -First 1
        if ($null -ne $candidate) { return $candidate }
        Start-Sleep -Milliseconds 50
    } until ([DateTime]::UtcNow -ge $deadline)
    throw "Could not resolve the broker child of MCP process $ParentProcessId."
}

function Invoke-BoundedMcpRequest {
    param([Parameter(Mandatory)][string]$Method, [hashtable]$Params = @{}, [int]$ResponseTimeoutMs = 25000)
    $script:nextId++
    $payload = [ordered]@{ jsonrpc = '2.0'; id = $script:nextId; method = $Method; params = $Params }
    $script:mcp.StandardInput.WriteLine(($payload | ConvertTo-Json -Depth 20 -Compress))
    $script:mcp.StandardInput.Flush()
    $readTask = $script:mcp.StandardOutput.ReadLineAsync()
    if (-not $readTask.Wait($ResponseTimeoutMs)) { throw "MCP did not reply to $Method within $ResponseTimeoutMs ms." }
    $line = $readTask.GetAwaiter().GetResult()
    if ([string]::IsNullOrWhiteSpace($line)) { throw "MCP closed before replying to $Method." }
    $response = $line | ConvertFrom-Json
    if ($null -ne $response.error) { throw "MCP error: $($response.error.message)" }
    return $response.result
}

try {
    $testApp = Start-Process -FilePath $testAppPath -PassThru
    $windowDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 50
        $testApp.Refresh()
    } until ($testApp.MainWindowHandle -ne 0 -or [DateTime]::UtcNow -ge $windowDeadline)
    if ($testApp.MainWindowHandle -eq 0) { throw 'Test app did not create a visible window.' }

    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $mcpPath
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.EnvironmentVariables['WCU_BROKER_PATH'] = $brokerPath
    $start.EnvironmentVariables['WCU_BROKER_CALL_TIMEOUT_MS'] = '2000'
    $mcp = [System.Diagnostics.Process]::new()
    $mcp.StartInfo = $start
    if (-not $mcp.Start()) { throw 'Could not start MCP process.' }
    $stderrTask = $mcp.StandardError.ReadToEndAsync()

    Invoke-BoundedMcpRequest -Method 'initialize' -Params @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'broker-recovery-smoke'; version = '1.0' } } | Out-Null
    $before = Get-ChildBrokerProcess -ParentProcessId $mcp.Id
    $listed = Invoke-BoundedMcpRequest -Method 'tools/call' -Params @{ name = 'list_windows'; arguments = @{} }
    $listedPayload = $listed.content[0].text | ConvertFrom-Json
    $target = @($listedPayload.windows | Where-Object { $_.title -eq 'Windows Computer Use Test App' })
    if ($target.Count -ne 1) { throw "Expected one recovery test window, found $($target.Count)." }
    $windowId = [long]$target[0].id
    $mouseX = [int]$target[0].bounds.x + 20
    $mouseY = [int]$target[0].bounds.y + 20
    $heldKey = Invoke-BoundedMcpRequest -Method 'tools/call' -Params @{ name = 'key_down'; arguments = @{ window_id = $windowId; key = 'ctrl' } }
    if ($heldKey.isError) { throw "Could not stage a held Ctrl key: $($heldKey.content[0].text)" }
    $heldMouse = Invoke-BoundedMcpRequest -Method 'tools/call' -Params @{ name = 'mouse_down'; arguments = @{ x = $mouseX; y = $mouseY; coordinate_space = 'screen'; button = 'left' } }
    if ($heldMouse.isError) { throw "Could not stage a held left mouse button: $($heldMouse.content[0].text)" }
    if (-not [WcuRecoveryInputProbe]::IsDown(0x11) -or -not [WcuRecoveryInputProbe]::IsDown(0x01)) {
        throw 'The native input probe did not observe the staged held Ctrl key and left mouse button.'
    }

    $missingTitle = "WCU recovery probe $([guid]::NewGuid().ToString('N'))"
    $timedOut = Invoke-BoundedMcpRequest -Method 'tools/call' -Params @{ name = 'wait_for_window'; arguments = @{ title = $missingTitle; state = 'exists'; timeout_ms = 5000; poll_ms = 50 } }
    if (-not $timedOut.isError -or $timedOut.content[0].text -notmatch "exceeded 2000 ms" -or $timedOut.content[0].text -notmatch 'broker was restarted') {
        throw "Broker deadline did not return the explicit unknown-state recovery error: $($timedOut | ConvertTo-Json -Depth 10 -Compress)"
    }

    $after = Get-ChildBrokerProcess -ParentProcessId $mcp.Id
    if ([int]$after.ProcessId -eq [int]$before.ProcessId) { throw 'Broker process id did not change after the forced deadline.' }
    if ([WcuRecoveryInputProbe]::IsDown(0x11) -or [WcuRecoveryInputProbe]::IsDown(0x01)) {
        throw 'Broker recovery left a plugin-held Ctrl key or left mouse button down.'
    }
    $windows = Invoke-BoundedMcpRequest -Method 'tools/call' -Params @{ name = 'list_windows'; arguments = @{} }
    if ($windows.isError) { throw "The restarted broker did not serve the next call: $($windows.content[0].text)" }
    $windowPayload = $windows.content[0].text | ConvertFrom-Json
    if ($null -eq $windowPayload.windows) { throw 'The post-recovery list_windows response was malformed.' }
    Start-Sleep -Milliseconds 100
    if ($null -ne (Get-Process -Id ([int]$before.ProcessId) -ErrorAction SilentlyContinue)) { throw 'The timed-out broker process remained alive after recovery.' }

    [ordered]@{
        ok = $true
        deadline_ms = 2000
        old_broker_pid = [int]$before.ProcessId
        new_broker_pid = [int]$after.ProcessId
        next_call_succeeded = $true
        action_state_reported_unknown = $true
        held_key_released = $true
        held_mouse_released = $true
    } | ConvertTo-Json -Compress
} finally {
    [WcuRecoveryInputProbe]::ReleaseOwnedInput()
    if ($null -ne $mcp) {
        try { $mcp.StandardInput.Close() } catch {}
        if (-not $mcp.WaitForExit(2000)) { try { $mcp.Kill() } catch {} }
        if ($null -ne $stderrTask) { try { $stderrTask.GetAwaiter().GetResult() | Out-Null } catch {} }
        $mcp.Dispose()
    }
    if ($null -ne $testApp) {
        try { $testApp.CloseMainWindow() | Out-Null } catch {}
        if (-not $testApp.WaitForExit(1500)) { try { $testApp.Kill() } catch {} }
        $testApp.Dispose()
    }
}
