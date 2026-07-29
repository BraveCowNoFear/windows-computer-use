$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8
[Console]::InputEncoding = $utf8
$OutputEncoding = $utf8
$pluginRoot = Split-Path -Parent $PSScriptRoot
$mcp = Join-Path $pluginRoot 'dist\win-x64\mcp\WindowsComputerUse.Mcp.exe'
$broker = Join-Path $pluginRoot 'dist\win-x64\broker\WindowsComputerUse.Broker.exe'
if (-not (Test-Path -LiteralPath $mcp) -or -not (Test-Path -LiteralPath $broker)) {
    $buildOutput = @(& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release 2>&1)
    if ($buildOutput.Count -gt 0) { [Console]::Error.WriteLine(($buildOutput | Out-String)) }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$env:WCU_PLUGIN_ROOT = $pluginRoot
$env:WCU_BROKER_PATH = $broker
& $mcp
exit $LASTEXITCODE
