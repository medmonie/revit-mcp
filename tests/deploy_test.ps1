$ErrorActionPreference = 'Stop'
$deploy = Join-Path $PSScriptRoot '..\tools\deploy_addin.ps1'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$caseName = 'RevitDeployTest-' + [guid]::NewGuid().ToString('N')
$caseDir = [IO.Directory]::CreateDirectory((Join-Path $tempRoot $caseName)).FullName
try {
    $dll = Join-Path $caseDir 'source.dll'
    $manifest = Join-Path $caseDir 'source.addin'
    $dest = Join-Path $caseDir 'test addins'
    $installedDll = Join-Path $dest 'RevitMcpBridge\RevitMcpBridge.dll'
    $installedManifest = Join-Path $dest 'RevitMcpBridge.addin'
    [IO.File]::WriteAllText($dll, 'original DLL')
    [IO.File]::WriteAllText($manifest, 'original manifest')
    & $deploy $dll $manifest $dest
    [IO.File]::WriteAllText($dll, 'new DLL')
    [IO.File]::WriteAllText($manifest, 'new manifest')
    $loaded = [IO.File]::Open($installedDll, 'Open', 'Read', ([IO.FileShare]::Read -bor [IO.FileShare]::Delete))
    try {
        $held = [IO.File]::Open($installedManifest, 'Open', 'Read', 'Read')
        $failed = $false
        try { & $deploy $dll $manifest $dest } catch { $failed = $true } finally { $held.Dispose() }
        if (!$failed -or [IO.File]::ReadAllText($installedDll) -ne 'original DLL' -or
            [IO.File]::ReadAllText($installedManifest) -ne 'original manifest') {
            throw 'Failed replacement did not preserve the original DLL and manifest.'
        }
        & $deploy $dll $manifest $dest
        if ([IO.File]::ReadAllText($installedDll) -ne 'new DLL' -or
            [IO.File]::ReadAllText($installedManifest) -ne 'new manifest') { throw 'Retry failed.' }
    } finally { $loaded.Dispose() }
    Write-Output 'Deployment install, rollback and retry passed.'
} finally {
    $resolved = (Resolve-Path -LiteralPath $caseDir).ProviderPath
    if ($resolved -ne [IO.Path]::Combine($tempRoot, $caseName)) { throw 'Unexpected test directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
