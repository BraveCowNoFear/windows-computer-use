param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
$pluginRoot = Split-Path -Parent $PSScriptRoot
$dotnetCandidates = @(
    'C:\Users\Clr\.codex\tools\dotnet-sdk-8\dotnet.exe',
    (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
)
$dotnet = $dotnetCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $dotnet) { throw 'A .NET 8 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/8.0.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

& $dotnet restore (Join-Path $pluginRoot 'WindowsComputerUse.sln')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet build (Join-Path $pluginRoot 'WindowsComputerUse.sln') -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$dist = Join-Path $pluginRoot 'dist\win-x64'
& $dotnet publish (Join-Path $pluginRoot 'src\WindowsComputerUse.Broker\WindowsComputerUse.Broker.csproj') -c $Configuration -r win-x64 --self-contained false -o (Join-Path $dist 'broker')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet publish (Join-Path $pluginRoot 'src\WindowsComputerUse.Mcp\WindowsComputerUse.Mcp.csproj') -c $Configuration -r win-x64 --self-contained false -o (Join-Path $dist 'mcp')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Output ([ordered]@{ ok = $true; configuration = $Configuration; dist = $dist } | ConvertTo-Json -Compress)
