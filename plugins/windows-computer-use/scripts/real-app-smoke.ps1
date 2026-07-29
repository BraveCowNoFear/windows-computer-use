$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$mcpPath = Join-Path $pluginRoot 'dist\win-x64\mcp\WindowsComputerUse.Mcp.exe'
$brokerPath = Join-Path $pluginRoot 'dist\win-x64\broker\WindowsComputerUse.Broker.exe'
if (-not (Test-Path -LiteralPath $mcpPath) -or -not (Test-Path -LiteralPath $brokerPath)) {
    throw 'Missing published MCP/Broker. Run scripts/build.ps1 first.'
}

$mcp = $null
$mcpErrorTask = $null
$nextId = 0
$openedWindows = [System.Collections.Generic.List[object]]::new()
$artifacts = [System.Collections.Generic.List[string]]::new()
$explorerFolder = Join-Path $env:TEMP ("wcu-explorer-{0}" -f [guid]::NewGuid().ToString('N'))

function Invoke-McpRequest {
    param([string]$Method, [hashtable]$Params = @{})
    $script:nextId++
    $payload = [ordered]@{ jsonrpc = '2.0'; id = $script:nextId; method = $Method; params = $Params }
    $script:mcp.StandardInput.WriteLine(($payload | ConvertTo-Json -Depth 20 -Compress))
    $script:mcp.StandardInput.Flush()
    $line = $script:mcp.StandardOutput.ReadLine()
    if ($null -eq $line) {
        $stderr = if ($null -ne $script:mcpErrorTask -and $script:mcpErrorTask.IsCompleted) { $script:mcpErrorTask.GetAwaiter().GetResult() } else { 'MCP stderr is still draining.' }
        throw "MCP closed unexpectedly: $stderr"
    }
    $response = $line | ConvertFrom-Json
    if ($null -ne $response.error) { throw $response.error.message }
    return $response.result
}

function Invoke-WcuTool {
    param([string]$Name, [hashtable]$Arguments = @{})
    $result = Invoke-McpRequest -Method 'tools/call' -Params @{ name = $Name; arguments = $Arguments }
    if ($result.isError) { throw "Tool $Name failed: $($result.content[0].text)" }
    if ($Name -eq 'capture') { return $result }
    return ($result.content[0].text | ConvertFrom-Json)
}

function Get-WcuWindows { return @((Invoke-WcuTool -Name 'list_windows').windows) }

function Wait-NewWindow {
    param([long[]]$Before, [string]$AppPattern, [string]$TitlePattern = '', [int]$TimeoutSeconds = 12)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 150
        $matches = @(Get-WcuWindows | Where-Object {
            $isNew = $Before -notcontains [long]$_.id
            $appMatch = $_.app -match $AppPattern -or $_.processPath -match $AppPattern
            $titleMatch = [string]::IsNullOrWhiteSpace($TitlePattern) -or $_.title -match $TitlePattern
            $isNew -and $appMatch -and $titleMatch
        })
        if ($matches.Count -eq 1) { return $matches[0] }
    } until ([DateTime]::UtcNow -ge $deadline)
    return $null
}

function Test-WcuWindow {
    param([string]$Label, [object]$Window, [bool]$CloseAfter = $true)
    $id = [long]$Window.id
    $lease = [pscustomobject]@{ id = $id; title = [string]$Window.title }
    if ($CloseAfter) { $script:openedWindows.Add($lease) }
    try {
    $inspection = Invoke-WcuTool -Name 'inspect_window' -Arguments @{ window_id = $id; limit = 800 }
    if (@($inspection.controls).Count -eq 0) { throw "$Label exposed no UIA controls." }
    $capturePath = Join-Path $env:TEMP ("wcu-real-{0}-{1}.png" -f $Label.ToLowerInvariant(), [guid]::NewGuid().ToString('N'))
    $script:artifacts.Add($capturePath)
    $capture = Invoke-WcuTool -Name 'capture' -Arguments @{ window_id = $id; path = $capturePath }
    $captureMeta = $capture.content[0].text | ConvertFrom-Json
    $ocr = Invoke-WcuTool -Name 'ocr' -Arguments @{ path = $capturePath }
    if (-not $ocr.ok -or [string]::IsNullOrWhiteSpace([string]$ocr.text)) {
        Start-Sleep -Milliseconds 300
        $capture = Invoke-WcuTool -Name 'capture' -Arguments @{ window_id = $id; path = $capturePath }
        $captureMeta = $capture.content[0].text | ConvertFrom-Json
        $ocr = Invoke-WcuTool -Name 'ocr' -Arguments @{ path = $capturePath }
    }
    if (-not $ocr.ok -or [string]::IsNullOrWhiteSpace([string]$ocr.text)) {
        throw "$Label OCR failed after one fresh-capture retry: $($ocr.error)"
    }
    $closeState = 'not-requested'
    if ($CloseAfter) {
        $current = @(Get-WcuWindows | Where-Object { [long]$_.id -eq $id -and [string]$_.title -eq $lease.title })
        if ($current.Count -eq 0) {
            $closeState = 'already-closed-or-replaced'
        } else {
            try {
                Invoke-WcuTool -Name 'press_key' -Arguments @{ window_id = $id; key = 'alt+f4' } | Out-Null
            } catch {
                if ($_.Exception.Message -match 'No matching window was found|window id is stale') {
                    $closeState = 'closed-during-close-race'
                    $script:openedWindows.Remove($lease) | Out-Null
                    return [ordered]@{
                        app = $Label; ok = $true; window_id = $id; controls = @($inspection.controls).Count
                        capture_backend = $captureMeta.backend; ocr_ok = [bool]$ocr.ok
                        ocr_text_length = if ($null -ne $ocr.text) { $ocr.text.Length } else { 0 }
                        close_state = $closeState
                    }
                }
                throw
            }
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            do {
                Start-Sleep -Milliseconds 100
                $current = @(Get-WcuWindows | Where-Object { [long]$_.id -eq $id })
                $stillTarget = $current.Count -gt 0 -and [string]$current[0].title -eq $lease.title
            } until (-not $stillTarget -or [DateTime]::UtcNow -ge $deadline)
            if ($stillTarget) { throw "$Label window did not close or leave its original title after exact-window alt+f4." }
            $closeState = 'closed-or-title-replaced'
        }
        $script:openedWindows.Remove($lease) | Out-Null
    }
    return [ordered]@{
        app = $Label
        ok = $true
        window_id = $id
        controls = @($inspection.controls).Count
        capture_backend = $captureMeta.backend
        ocr_ok = [bool]$ocr.ok
        ocr_text_length = if ($null -ne $ocr.text) { $ocr.text.Length } else { 0 }
        close_state = $closeState
    }
    } catch {
        throw "$Label benchmark failed for window $id ($($lease.title)): $($_.Exception.Message)"
    }
}

try {
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $mcpPath
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.EnvironmentVariables['WCU_BROKER_PATH'] = $brokerPath
    $start.EnvironmentVariables['WCU_PLUGIN_ROOT'] = $pluginRoot
    $start.EnvironmentVariables['WCU_REQUIRE_WGC'] = '1'
    $mcp = [System.Diagnostics.Process]::new()
    $mcp.StartInfo = $start
    if (-not $mcp.Start()) { throw 'Could not start MCP process.' }
    $mcpErrorTask = $mcp.StandardError.ReadToEndAsync()
    Invoke-McpRequest -Method 'initialize' -Params @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'real-app-smoke'; version = '1.0' } } | Out-Null

    $results = [System.Collections.Generic.List[object]]::new()

    $beforeWindows = Get-WcuWindows
    $existingNotepad = @($beforeWindows | Where-Object { $_.app -match 'notepad' -or $_.processPath -match 'notepad' })
    if ($existingNotepad.Count -gt 0) {
        $results.Add([ordered]@{ app = 'Notepad'; ok = $true; skipped = 'An existing Notepad window was preserved.'; existing_windows = $existingNotepad.Count })
    } else {
        $before = @($beforeWindows | ForEach-Object { [long]$_.id })
        Start-Process -FilePath 'notepad.exe' | Out-Null
        $notepad = Wait-NewWindow -Before $before -AppPattern 'notepad'
        if ($null -eq $notepad) { throw 'A new Notepad window was not discovered.' }
        $results.Add((Test-WcuWindow -Label 'Notepad' -Window $notepad))
    }

    New-Item -ItemType Directory -Path $explorerFolder | Out-Null
    $before = @(Get-WcuWindows | ForEach-Object { [long]$_.id })
    Start-Process -FilePath 'explorer.exe' -ArgumentList $explorerFolder | Out-Null
    $folderName = [regex]::Escape((Split-Path -Leaf $explorerFolder))
    $explorer = Wait-NewWindow -Before $before -AppPattern 'explorer' -TitlePattern $folderName
    if ($null -eq $explorer) { throw 'A new File Explorer benchmark window was not discovered.' }
    $results.Add((Test-WcuWindow -Label 'Explorer' -Window $explorer))

    $beforeWindows = Get-WcuWindows
    $before = @($beforeWindows | ForEach-Object { [long]$_.id })
    Start-Process 'ms-settings:about' | Out-Null
    $settingsTitlePattern = 'Settings|' + [char]0x8BBE + [char]0x7F6E
    $settings = Wait-NewWindow -Before $before -AppPattern 'SystemSettings|ApplicationFrameHost' -TitlePattern $settingsTitlePattern
    if ($null -ne $settings) {
        $results.Add((Test-WcuWindow -Label 'Settings' -Window $settings))
    } else {
        $reused = @(Get-WcuWindows | Where-Object {
            ($_.app -match 'SystemSettings|ApplicationFrameHost' -or $_.processPath -match 'SystemSettings|ApplicationFrameHost') -and $_.title -match $settingsTitlePattern
        })
        $reason = if ($reused.Count -gt 0) { 'Windows reused an existing Settings window; it was not inspected or closed.' } else { 'Settings did not expose a stable top-level window before the timeout.' }
        $results.Add([ordered]@{ app = 'Settings'; ok = $true; skipped = $reason; existing_windows = $reused.Count })
    }

    Invoke-WcuTool -Name 'end_session' | Out-Null
    [ordered]@{ ok = $true; results = $results } | ConvertTo-Json -Depth 8
} finally {
    if ($null -ne $mcp) {
        foreach ($lease in @($openedWindows)) {
            try {
                $current = @(Get-WcuWindows | Where-Object { [long]$_.id -eq [long]$lease.id -and [string]$_.title -eq [string]$lease.title })
                if ($current.Count -eq 1) { Invoke-WcuTool -Name 'press_key' -Arguments @{ window_id = [long]$lease.id; key = 'alt+f4' } | Out-Null }
            } catch {}
        }
        try { $mcp.StandardInput.Close() } catch {}
        if (-not $mcp.WaitForExit(2000)) { try { $mcp.Kill() } catch {} }
        if ($null -ne $mcpErrorTask) { try { $mcpErrorTask.GetAwaiter().GetResult() | Out-Null } catch {} }
        $mcp.Dispose()
    }
    foreach ($artifact in @($artifacts)) { Remove-Item -LiteralPath $artifact -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $explorerFolder) {
        $resolved = [IO.Path]::GetFullPath($explorerFolder)
        $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
        if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
