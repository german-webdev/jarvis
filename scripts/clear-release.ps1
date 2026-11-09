# Release output contains only the current installer, checksum and readme.
# Deliberately accept no path argument: never clean payloads, profiles or a caller-selected folder.
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'release'))
if ($releaseRoot -ne (Join-Path $projectRoot 'artifacts\release')) { throw 'Unexpected release directory' }
foreach ($folder in @($artifactsRoot, $releaseRoot)) {
    if ((Test-Path -LiteralPath $folder) -and ((Get-Item -LiteralPath $folder -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to clean a linked release directory'
    }
}
if (-not (Test-Path -LiteralPath $releaseRoot)) {
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    return
}
$resolvedRelease = (Resolve-Path -LiteralPath $releaseRoot).Path
if ($resolvedRelease -ne $releaseRoot) { throw 'Release path resolved outside its expected location' }
$entries = @(Get-ChildItem -LiteralPath $resolvedRelease -Force)
foreach ($entry in $entries) {
    if ($entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $entry.DirectoryName -ne $resolvedRelease) {
        throw 'Unexpected directory or linked item in release output; nothing removed'
    }
}
foreach ($entry in $entries) { Remove-Item -LiteralPath $entry.FullName -Force }
Write-Output ('Cleared release output: ' + $entries.Count + ' files.')
