# Adds (or removes) the "revit" MCP server in the local MCP clients' configuration files.
#   configure_clients.ps1 -Exe <path to revit-mcp-server.exe>     add / update
#   configure_clients.ps1 -Remove                                 remove
# Claude Desktop: claude_desktop_config.json (normal install and Microsoft Store install). Other servers (civil3d ...)
# and settings are kept. Codex: ~/.codex/config.toml, [mcp_servers.revit] table only. Each file is backed up first.
# ChatGPT desktop keeps its MCP servers in its own settings screen, so it is not changed here.
param(
    [string]$Exe,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$done = @()

function Backup($path) {
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination "$path.bak-$stamp" -Force }
}

# ---------------------------------------------------------------- Claude Desktop
$claudeFiles = @()
$roaming = Join-Path $env:APPDATA 'Claude'
if (Test-Path -LiteralPath $roaming) { $claudeFiles += (Join-Path $roaming 'claude_desktop_config.json') }
Get-ChildItem -Path (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'Claude_*' -ErrorAction SilentlyContinue | ForEach-Object {
    $store = Join-Path $_.FullName 'LocalCache\Roaming\Claude'
    if (Test-Path -LiteralPath $store) { $claudeFiles += (Join-Path $store 'claude_desktop_config.json') }
}

foreach ($file in $claudeFiles) {
    try {
        $config = $null
        if (Test-Path -LiteralPath $file) {
            $text = [IO.File]::ReadAllText($file)
            if ($text.Trim()) { $config = $text | ConvertFrom-Json }
        }
        if (-not $config) {
            if ($Remove) { continue }
            $config = New-Object PSObject
        }
        if (-not ($config.PSObject.Properties.Name -contains 'mcpServers')) {
            if ($Remove) { continue }
            $config | Add-Member -NotePropertyName 'mcpServers' -NotePropertyValue (New-Object PSObject)
        }
        $servers = $config.mcpServers
        if ($Remove) {
            if (-not ($servers.PSObject.Properties.Name -contains 'revit')) { continue }
            Backup $file
            $servers.PSObject.Properties.Remove('revit')
        } else {
            Backup $file
            $entry = New-Object PSObject
            $entry | Add-Member -NotePropertyName 'command' -NotePropertyValue $Exe
            if ($servers.PSObject.Properties.Name -contains 'revit') { $servers.revit = $entry }
            else { $servers | Add-Member -NotePropertyName 'revit' -NotePropertyValue $entry }
        }
        [IO.File]::WriteAllText($file, ($config | ConvertTo-Json -Depth 32), $utf8)
        $done += "Claude Desktop: $file"
    } catch {
        Write-Warning "Could not update $file ($($_.Exception.Message)). Add the revit entry by hand (see INSTALL.txt)."
    }
}

# ---------------------------------------------------------------- Codex
$codex = Join-Path $env:USERPROFILE '.codex\config.toml'
if (Test-Path -LiteralPath $codex) {
    try {
        $lines = [IO.File]::ReadAllLines($codex)
        $kept = New-Object System.Collections.Generic.List[string]
        $inRevit = $false
        $hadRevit = $false
        foreach ($line in $lines) {
            $t = $line.Trim()
            if ($t.StartsWith('[')) {
                $inRevit = ($t -eq '[mcp_servers.revit]' -or $t.StartsWith('[mcp_servers.revit.'))
                if ($inRevit) { $hadRevit = $true }
            }
            if (-not $inRevit) { $kept.Add($line) }
        }
        if ($hadRevit -or -not $Remove) {
            Backup $codex
            while ($kept.Count -gt 0 -and -not $kept[$kept.Count - 1].Trim()) { $kept.RemoveAt($kept.Count - 1) }
            if (-not $Remove) {
                $kept.Add('')
                $kept.Add('[mcp_servers.revit]')
                $kept.Add('command = ' + ($Exe | ConvertTo-Json -Compress))
                $kept.Add('')
                $kept.Add('[mcp_servers.revit.env]')
                $kept.Add('REVIT_MCP_SPLIT = "1"')
            }
            [IO.File]::WriteAllLines($codex, $kept.ToArray(), $utf8)
            $done += "Codex: $codex"
        }
    } catch {
        Write-Warning "Could not update $codex ($($_.Exception.Message))."
    }
}

if ($done.Count -eq 0) {
    if ($Remove) { Write-Output 'No MCP client configuration contained the revit server.' }
    else { Write-Output 'No Claude Desktop or Codex configuration was found on this PC. Add the revit entry by hand (see INSTALL.txt).' }
} else {
    foreach ($d in $done) { Write-Output ("  " + $(if ($Remove) { 'removed from ' } else { 'configured ' }) + $d) }
}
