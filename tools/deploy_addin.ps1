param(
    [Parameter(Mandatory)][string]$Dll,
    [Parameter(Mandatory)][string]$Manifest,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Destination)
$bin = [IO.Directory]::CreateDirectory((Join-Path $root 'RevitMcpBridge')).FullName
$tag = [guid]::NewGuid().ToString('N')
$lock = [IO.FileStream]::new((Join-Path $bin 'deploy.lock'), [IO.FileMode]::OpenOrCreate,
    [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose)
$files = @(
    @{ Source = $Dll; Target = Join-Path $bin 'RevitMcpBridge.dll' },
    @{ Source = $Manifest; Target = Join-Path $root 'RevitMcpBridge.addin' }
)
try {
    foreach ($file in $files) {
        $file.Stage = "$($file.Target).stage-$tag"
        $file.Backup = "$($file.Target).old-$tag"
        [IO.File]::Copy($file.Source, $file.Stage)
    }
    foreach ($file in $files) {
        if ([IO.File]::Exists($file.Target)) {
            [IO.File]::Move($file.Target, $file.Backup)
            $file.Saved = $true
        }
        [IO.File]::Move($file.Stage, $file.Target)
        $file.Installed = $true
    }
    # On a later successful build, also remove backups that were previously held open by Revit.
    foreach ($file in $files) {
        $leaf = [IO.Path]::GetFileName($file.Target)
        try {
            foreach ($old in [IO.Directory]::GetFiles([IO.Path]::GetDirectoryName($file.Target), "$leaf.old*")) {
                if ([IO.Path]::GetFileName($old) -match ('^' + [regex]::Escape($leaf) + '\.old(?:-[0-9a-f]{32}|[0-9a-f]{8})$')) {
                    try { [IO.File]::Delete($old) } catch { Write-Warning "Previous version retained at $old." }
                }
            }
        } catch { Write-Warning "Could not check previous versions of $($file.Target). $_" }
    }
} catch {
    $failure = $_
    for ($i = $files.Count - 1; $i -ge 0; $i--) {
        $file = $files[$i]
        try {
            if ($file.Installed) { [IO.File]::Move($file.Target, $file.Stage) }
            if ($file.Saved) { [IO.File]::Move($file.Backup, $file.Target) }
        } catch { Write-Warning "Could not restore $($file.Target). Backup path $($file.Backup). $_" }
    }
    throw $failure
} finally {
    foreach ($file in $files) {
        if ($file.Stage -and [IO.File]::Exists($file.Stage)) {
            try { [IO.File]::Delete($file.Stage) } catch { Write-Warning $_ }
        }
    }
    $lock.Dispose()
}
Write-Output "Deployed RevitMcpBridge to $root"
