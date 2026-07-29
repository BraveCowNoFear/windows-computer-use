param([switch]$IncludeWeChat, [switch]$IncludeSolidWorks, [switch]$OptionalOnly)

$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$mcpPath = Join-Path $pluginRoot 'dist\win-x64\mcp\WindowsComputerUse.Mcp.exe'
$brokerPath = Join-Path $pluginRoot 'dist\win-x64\broker\WindowsComputerUse.Broker.exe'
foreach ($requiredPath in @($mcpPath, $brokerPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) { throw "Missing build output: $requiredPath. Run scripts/build.ps1 first." }
}

$mcp = $null
$nextId = 0
$artifacts = [System.Collections.Generic.List[string]]::new()
$profiles = [System.Collections.Generic.List[string]]::new()
$startedProcessGroups = [System.Collections.Generic.List[object]]::new()

function Invoke-McpRequest {
    param([string]$Method, [hashtable]$Params = @{})
    $script:nextId++
    $payload = [ordered]@{ jsonrpc = '2.0'; id = $script:nextId; method = $Method; params = $Params }
    $script:mcp.StandardInput.WriteLine(($payload | ConvertTo-Json -Depth 20 -Compress))
    $script:mcp.StandardInput.Flush()
    $line = $script:mcp.StandardOutput.ReadLine()
    if ($null -eq $line) { throw "MCP process closed before replying. $($script:mcp.StandardError.ReadToEnd())" }
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

function Get-WcuWindows { return @(Invoke-WcuTool -Name 'list_windows').windows }

function Get-FamilyProcesses {
    param([string[]]$Names)
    return @(Get-Process -Name $Names -ErrorAction SilentlyContinue)
}

function Wait-NewWindow {
    param([long[]]$Before, [string]$AppPattern, [string]$TitlePattern, [int]$TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 200
        $matches = @(Get-WcuWindows | Where-Object {
            $isNew = $Before -notcontains [long]$_.id
            $appMatch = $_.app -match $AppPattern -or $_.processPath -match $AppPattern
            $titleMatch = [string]::IsNullOrWhiteSpace($TitlePattern) -or $_.title -match $TitlePattern
            $readyTitle = $_.title -notmatch '^\s*(Opening|Starting)\b'
            $isNew -and $appMatch -and $titleMatch -and $readyTitle
        } | Sort-Object { [int64]$_.bounds.width * [int64]$_.bounds.height } -Descending)
        if ($matches.Count -gt 0) {
            $candidate = $matches[0]
            Start-Sleep -Milliseconds 800
            $stable = @(Get-WcuWindows | Where-Object { [long]$_.id -eq [long]$candidate.id })
            if ($stable.Count -eq 1 -and $stable[0].title -notmatch '^\s*(Opening|Starting)\b') { return $stable[0] }
        }
    } until ([DateTime]::UtcNow -ge $deadline)
    return $null
}

function Close-NewProcessGroup {
    param([object]$Group)
    $remaining = @(Get-FamilyProcesses -Names $Group.Names | Where-Object { $Group.BeforePids -notcontains $_.Id })
    foreach ($process in $remaining) { try { $process.CloseMainWindow() | Out-Null } catch {} }
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    do {
        Start-Sleep -Milliseconds 100
        $remaining = @(Get-FamilyProcesses -Names $Group.Names | Where-Object { $Group.BeforePids -notcontains $_.Id })
    } until ($remaining.Count -eq 0 -or [DateTime]::UtcNow -ge $deadline)
    foreach ($process in $remaining) { try { Stop-Process -Id $process.Id -Force -ErrorAction Stop } catch {} }
}

function Test-IsolatedApp {
    param(
        [string]$Label,
        [string]$FilePath,
        [string[]]$Arguments,
        [string[]]$ProcessNames,
        [string]$AppPattern,
        [string]$TitlePattern,
        [int]$TimeoutSeconds = 30
    )
    if (-not (Test-Path -LiteralPath $FilePath)) { return [ordered]@{ app = $Label; ok = $true; skipped = 'Not installed.' } }
    $existing = @(Get-FamilyProcesses -Names $ProcessNames)
    if ($existing.Count -gt 0) { return [ordered]@{ app = $Label; ok = $true; skipped = 'Existing process preserved.'; existing_processes = $existing.Count } }

    $beforeWindows = Get-WcuWindows
    $beforeIds = @($beforeWindows | ForEach-Object { [long]$_.id })
    $group = [pscustomobject]@{ Names = $ProcessNames; BeforePids = @($existing | ForEach-Object Id) }
    $script:startedProcessGroups.Add($group)
    if ($Arguments.Count -gt 0) { Start-Process -FilePath $FilePath -ArgumentList $Arguments | Out-Null }
    else { Start-Process -FilePath $FilePath | Out-Null }

    $window = Wait-NewWindow -Before $beforeIds -AppPattern $AppPattern -TitlePattern $TitlePattern -TimeoutSeconds $TimeoutSeconds
    if ($null -eq $window) { throw "$Label did not expose a stable new top-level window within $TimeoutSeconds seconds." }
    $id = [long]$window.id
    $capturePath = Join-Path $env:TEMP ("wcu-extended-{0}-{1}.png" -f ($Label -replace '[^a-zA-Z0-9]', '').ToLowerInvariant(), [guid]::NewGuid().ToString('N'))
    $script:artifacts.Add($capturePath)

    try {
        $snapshot = Invoke-WcuTool -Name 'snapshot' -Arguments @{ window_id = $id; limit = 1200; path = $capturePath }
    } catch {
        throw "$Label snapshot failed for window $id ($($window.title)): $($_.Exception.Message)"
    }
    $metadata = $snapshot.content[0].text | ConvertFrom-Json
    $controls = @($metadata.inspection.controls)
    if ($controls.Count -lt 1 -or $metadata.capture.backend -ne 'windows-graphics-capture') { throw "$Label did not pass hierarchical UIA + WGC." }
    $ocr = Invoke-WcuTool -Name 'ocr' -Arguments @{ path = $capturePath }
    if (-not $ocr.ok -or [string]::IsNullOrWhiteSpace([string]$ocr.text)) {
        Start-Sleep -Milliseconds 400
        $snapshot = Invoke-WcuTool -Name 'snapshot' -Arguments @{ window_id = $id; limit = 1200; path = $capturePath }
        $metadata = $snapshot.content[0].text | ConvertFrom-Json
        $ocr = Invoke-WcuTool -Name 'ocr' -Arguments @{ path = $capturePath }
    }
    if (-not $ocr.ok -or [string]::IsNullOrWhiteSpace([string]$ocr.text)) { throw "$Label OCR failed after one retry: $($ocr.error)" }

    try { Invoke-WcuTool -Name 'press_key' -Arguments @{ window_id = $id; key = 'alt+f4' } | Out-Null } catch {}
    Close-NewProcessGroup -Group $group
    return [ordered]@{
        app = $Label
        ok = $true
        window_id = $id
        controls = $controls.Count
        max_depth = ($controls | Measure-Object -Property depth -Maximum).Maximum
        capture_backend = $metadata.capture.backend
        ocr_ok = [bool]$ocr.ok
        ocr_text_length = ([string]$ocr.text).Length
        cleanup = 'new-process-group-closed'
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
    Invoke-McpRequest -Method 'initialize' -Params @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'extended-app-smoke'; version = '1.0' } } | Out-Null

    $display = Invoke-WcuTool -Name 'display_info'
    $results = [System.Collections.Generic.List[object]]::new()
    if (-not $OptionalOnly) {
        $results.Add((Test-IsolatedApp -Label 'Word' -FilePath 'C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE' -Arguments @('/x', '/q', '/n') -ProcessNames @('WINWORD') -AppPattern 'WINWORD' -TitlePattern 'Word|Document' -TimeoutSeconds 30))
        $results.Add((Test-IsolatedApp -Label 'Excel' -FilePath 'C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE' -Arguments @('/x') -ProcessNames @('EXCEL') -AppPattern 'EXCEL' -TitlePattern 'Excel|Book' -TimeoutSeconds 30))

        $codeProfile = Join-Path $env:TEMP ("wcu-vscode-profile-{0}" -f [guid]::NewGuid().ToString('N'))
        $codeExtensions = Join-Path $env:TEMP ("wcu-vscode-extensions-{0}" -f [guid]::NewGuid().ToString('N'))
        $profiles.Add($codeProfile)
        $profiles.Add($codeExtensions)
        $results.Add((Test-IsolatedApp -Label 'VSCode-Electron' -FilePath (Join-Path $env:LOCALAPPDATA 'Programs\Microsoft VS Code\Code.exe') -Arguments @('--new-window', '--disable-extensions', '--skip-welcome', '--disable-workspace-trust', '--user-data-dir', $codeProfile, '--extensions-dir', $codeExtensions) -ProcessNames @('Code') -AppPattern 'Code' -TitlePattern 'Visual Studio Code' -TimeoutSeconds 40))
    }

    if ($IncludeWeChat) {
        $results.Add((Test-IsolatedApp -Label 'WeChat' -FilePath 'C:\Program Files (x86)\Tencent\Weixin\Weixin.exe' -Arguments @() -ProcessNames @('Weixin') -AppPattern 'Weixin' -TitlePattern '' -TimeoutSeconds 40))
    }
    if ($IncludeSolidWorks) {
        $results.Add((Test-IsolatedApp -Label 'SolidWorks' -FilePath 'D:\Program Files\SOLIDWORKS Corp-0\SOLIDWORKS\SLDWORKS.exe' -Arguments @() -ProcessNames @('SLDWORKS') -AppPattern 'SLDWORKS' -TitlePattern 'SOLIDWORKS' -TimeoutSeconds 100))
    }

    Invoke-WcuTool -Name 'end_session' | Out-Null
    [ordered]@{
        ok = $true
        display_count = @($display.displays).Count
        primary_scale_percent = @($display.displays | Where-Object isPrimary)[0].scalePercent
        results = $results
    } | ConvertTo-Json -Depth 8
} finally {
    foreach ($group in @($startedProcessGroups)) { Close-NewProcessGroup -Group $group }
    if ($null -ne $mcp) {
        try { $mcp.StandardInput.Close() } catch {}
        if (-not $mcp.WaitForExit(2000)) { try { $mcp.Kill() } catch {} }
        $mcp.Dispose()
    }
    foreach ($artifact in @($artifacts)) { Remove-Item -LiteralPath $artifact -Force -ErrorAction SilentlyContinue }
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    foreach ($profile in @($profiles)) {
        if (Test-Path -LiteralPath $profile) {
            $resolved = [IO.Path]::GetFullPath($profile)
            if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
        }
    }
}
