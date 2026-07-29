$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $pluginRoot '..\..'))

function Read-JsonFile {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing required JSON file: $Path" }
    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Resolve-PluginPath {
    param([Parameter(Mandatory)][string]$RelativePath)
    return [System.IO.Path]::GetFullPath((Join-Path $pluginRoot $RelativePath))
}

$manifestPath = Join-Path $pluginRoot '.codex-plugin\plugin.json'
$mcpPath = Join-Path $pluginRoot '.mcp.json'
$marketplacePath = Join-Path $repoRoot '.agents\plugins\marketplace.json'
$manifest = Read-JsonFile $manifestPath
$mcp = Read-JsonFile $mcpPath
$marketplace = Read-JsonFile $marketplacePath

if ($manifest.name -ne 'windows-computer-use') { throw 'Plugin manifest name must be windows-computer-use.' }
if ([string]$manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw "Plugin version is not semantic: $($manifest.version)" }
foreach ($relative in @($manifest.skills, $manifest.mcpServers, $manifest.interface.composerIcon, $manifest.interface.logo)) {
    $resolved = Resolve-PluginPath ([string]$relative)
    if (-not (Test-Path -LiteralPath $resolved)) { throw "Manifest resource does not exist: $relative" }
}

$servers = @($mcp.mcpServers.psobject.Properties)
if ($servers.Count -ne 1 -or $servers[0].Name -ne 'windowsComputerUse') { throw 'Expected exactly one windowsComputerUse MCP server.' }
$server = $servers[0].Value
if ($server.command -ne 'powershell.exe') { throw 'The MCP command must be powershell.exe on Windows.' }
$fileFlag = [Array]::IndexOf([object[]]$server.args, '-File')
if ($fileFlag -lt 0 -or $fileFlag + 1 -ge $server.args.Count) { throw 'The MCP command is missing its -File launcher.' }
$launcher = Resolve-PluginPath ([string]$server.args[$fileFlag + 1])
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) { throw "MCP launcher does not exist: $launcher" }

$entries = @($marketplace.plugins | Where-Object { $_.name -eq $manifest.name })
if ($entries.Count -ne 1) { throw 'Marketplace must contain exactly one windows-computer-use entry.' }
$marketplacePluginPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot ([string]$entries[0].source.path)))
if ($marketplacePluginPath.TrimEnd('\') -ne $pluginRoot.TrimEnd('\')) { throw "Marketplace source does not resolve to the plugin root: $marketplacePluginPath" }

$skillPath = Join-Path (Resolve-PluginPath ([string]$manifest.skills)) 'windows-computer-use\SKILL.md'
if (-not (Test-Path -LiteralPath $skillPath -PathType Leaf)) { throw "Skill entrypoint is missing: $skillPath" }
$skillHeader = Get-Content -LiteralPath $skillPath -Raw -Encoding UTF8
if ($skillHeader -notmatch '(?m)^name:\s*windows-computer-use\s*$') { throw 'Skill frontmatter name does not match the plugin.' }

$syntaxFailures = [System.Collections.Generic.List[string]]::new()
$scripts = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File)
foreach ($script in $scripts) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors)
    foreach ($error in @($errors)) { $syntaxFailures.Add("$($script.Name): $($error.Message)") }
}
if ($syntaxFailures.Count -gt 0) { throw "PowerShell syntax validation failed: $($syntaxFailures -join '; ')" }

[ordered]@{
    ok = $true
    plugin = $manifest.name
    version = $manifest.version
    mcp_server = $servers[0].Name
    powershell_scripts = $scripts.Count
    marketplace = $marketplace.name
} | ConvertTo-Json -Compress
