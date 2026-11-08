param([switch]$Test)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    $env:DOTNET_ROOT = Join-Path $repoRoot '.tools\dotnet'
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools\dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:NUGET_PACKAGES = Join-Path $repoRoot '.tools\nuget'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $repoRoot '.tools\nuget-http'
    # Only this build process uses an isolated NuGet settings location.
    $originalAppData = $env:APPDATA
    $env:APPDATA = Join-Path $repoRoot '.tools\build-appdata'
    New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null
    $dotnetExe = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    & $dotnetExe restore Jarvis.slnx --configfile NuGet.Config --locked-mode --disable-parallel -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    if ($Test) {
        & $dotnetExe test Jarvis.slnx --no-restore -m:1 -c Release
        if ($LASTEXITCODE -ne 0) { throw '.NET tests failed.' }
        & '.\.venv\Scripts\python.exe' -m pytest -q
        if ($LASTEXITCODE -ne 0) { throw 'Python tests failed.' }
        & '.\.venv\Scripts\python.exe' -m ruff check speech scripts tests/python
        if ($LASTEXITCODE -ne 0) { throw 'Python checks failed.' }
    }
    & $dotnetExe publish src/Jarvis.App/Jarvis.App.csproj --no-restore -c Release -o artifacts/prototype
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed. Close JARVIS before rebuilding.' }
}
finally {
    if ($originalAppData) { $env:APPDATA = $originalAppData }
    Pop-Location
}
