# Creates (or updates) a GitHub release of medmonie/revit-mcp and attaches the installer zip.
#   powershell -File tools\publish_release.ps1 -Notes tools\release_notes.md -Asset "dist\RevitMcp.zip,dist\RevitMcp-source.zip"
# The tag is v<BRIDGE_VERSION from server\catalog.py> unless -Tag is given.
# Uses the GitHub sign-in that Git Credential Manager stored at the first git push (no token to type or store).
param(
    [string]$Tag,
    [Parameter(Mandatory)][string]$Notes,
    [Parameter(Mandatory)][string]$Asset,
    [string]$Repo = 'medmonie/revit-mcp',
    [string]$Title
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not $Tag) {
    $m = Select-String -Path (Join-Path $PSScriptRoot '..\server\catalog.py') -Pattern 'BRIDGE_VERSION\s*=\s*"([^"]+)"' | Select-Object -First 1
    if (-not $m) { throw 'BRIDGE_VERSION not found in server\catalog.py' }
    $Tag = 'v' + $m.Matches[0].Groups[1].Value
}
$files = $Asset -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }

# The stored GitHub credential (the same one git push uses).
$cred = "protocol=https`nhost=github.com`n`n" | git credential fill
$token = ($cred | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
if (-not $token) { throw 'No GitHub sign-in is stored yet. Run publish_to_github.bat once (it signs in), then run this again.' }
$headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'; 'User-Agent' = 'revit-mcp-release' }
$api = "https://api.github.com/repos/$Repo"

$body = [IO.File]::ReadAllText((Resolve-Path $Notes))
if (-not $Title) { $Title = "Revit MCP bridge $Tag" }

# Reuse the release if this tag already has one, else create it on main.
$release = $null
try { $release = Invoke-RestMethod -Method Get -Uri "$api/releases/tags/$Tag" -Headers $headers } catch { }
$payload = @{ tag_name = $Tag; target_commitish = 'main'; name = $Title; body = $body; draft = $false; prerelease = $false } | ConvertTo-Json
if ($release) {
    $release = Invoke-RestMethod -Method Patch -Uri "$api/releases/$($release.id)" -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -ContentType 'application/json'
    Write-Output "Updated release $Tag"
} else {
    $release = Invoke-RestMethod -Method Post -Uri "$api/releases" -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -ContentType 'application/json'
    Write-Output "Created release $Tag"
}

foreach ($file in $files) {
    $path = (Resolve-Path $file).Path
    $name = [IO.Path]::GetFileName($path)
    foreach ($old in @($release.assets | Where-Object { $_.name -eq $name })) {
        Invoke-RestMethod -Method Delete -Uri "$api/releases/assets/$($old.id)" -Headers $headers | Out-Null
    }
    $upload = "https://uploads.github.com/repos/$Repo/releases/$($release.id)/assets?name=$([Uri]::EscapeDataString($name))"
    $asset = Invoke-RestMethod -Method Post -Uri $upload -Headers $headers -InFile $path -ContentType 'application/zip'
    Write-Output ("Uploaded {0} ({1:N1} MB)" -f $asset.name, ($asset.size / 1MB))
}
Write-Output "Release page: $($release.html_url)"
