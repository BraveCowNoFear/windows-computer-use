param([switch]$Cold)

$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $PSScriptRoot 'run-mcp.ps1'
$dist = Join-Path $pluginRoot 'dist'
if ($Cold -and (Test-Path -LiteralPath $dist)) {
    $resolvedDist = [IO.Path]::GetFullPath($dist)
    $resolvedRoot = [IO.Path]::GetFullPath($pluginRoot).TrimEnd('\') + '\'
    if (-not $resolvedDist.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove generated output outside plugin root: $resolvedDist"
    }
    Remove-Item -LiteralPath $resolvedDist -Recurse -Force
}

$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = 'powershell.exe'
$start.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$launcher`""
$start.WorkingDirectory = $pluginRoot
$start.UseShellExecute = $false
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $false
$start.CreateNoWindow = $true
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $start
try {
    if (-not $process.Start()) { throw 'Could not start plugin MCP launcher.' }
    $initialize = [ordered]@{
        jsonrpc = '2.0'; id = 1; method = 'initialize'
        params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'launcher-smoke'; version = '1.0' } }
    } | ConvertTo-Json -Depth 10 -Compress
    $process.StandardInput.WriteLine($initialize)
    $process.StandardInput.Flush()
    $firstLine = $process.StandardOutput.ReadLine()
    if ([string]::IsNullOrWhiteSpace($firstLine)) { throw 'MCP launcher returned no initialize response.' }
    $initializeResponse = $firstLine | ConvertFrom-Json
    if ($initializeResponse.result.serverInfo.name -ne 'windows-computer-use') {
        throw "First stdout line was not a valid Windows Computer Use initialize response: $firstLine"
    }

    $process.StandardInput.WriteLine((@{ jsonrpc = '2.0'; id = 2; method = 'tools/list'; params = @{} } | ConvertTo-Json -Compress))
    $process.StandardInput.Flush()
    $toolsResponse = $process.StandardOutput.ReadLine() | ConvertFrom-Json
    $toolCount = @($toolsResponse.result.tools).Count
    if ($toolCount -ne 34) { throw "Expected 34 tools, got $toolCount." }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(5000)) { throw 'MCP launcher did not exit after stdin closed.' }
    [ordered]@{ ok = $true; cold = [bool]$Cold; first_stdout_was_jsonrpc = $true; tools = $toolCount; exit_code = $process.ExitCode } | ConvertTo-Json -Compress
} finally {
    if (-not $process.HasExited) { try { $process.Kill() } catch {} }
    $process.Dispose()
}
