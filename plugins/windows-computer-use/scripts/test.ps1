$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$dotnet = 'C:\Users\Clr\.codex\tools\dotnet-sdk-8\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
& $dotnet test (Join-Path $pluginRoot 'WindowsComputerUse.sln') -c Release --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $PSScriptRoot 'e2e-test.ps1') -RequireWgc
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $PSScriptRoot 'mcp-launch-smoke.ps1')
exit $LASTEXITCODE
