$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    New-Item -ItemType Directory -Force -Path '.tools','work' | Out-Null
    $env:UV_CACHE_DIR = Join-Path $repoRoot '.tools\uv-cache'
    $env:UV_PYTHON_INSTALL_DIR = Join-Path $repoRoot '.tools\python'
    $env:UV_LINK_MODE = 'copy'
    $env:UV_PYTHON_BIN_DIR = Join-Path $repoRoot '.tools\python-bin'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $uvExe = Join-Path $repoRoot '.tools\uv\uv.exe'
    if (-not (Test-Path -LiteralPath $uvExe)) {
        $uvCommand = Get-Command uv -ErrorAction SilentlyContinue
        if ($uvCommand) { $uvExe = $uvCommand.Source }
        else {
            Invoke-WebRequest -Uri 'https://github.com/astral-sh/uv/releases/download/0.10.12/uv-x86_64-pc-windows-msvc.zip' -OutFile 'work\uv.zip'
            Expand-Archive -LiteralPath 'work\uv.zip' -DestinationPath '.tools\uv' -Force
        }
    }
    & $uvExe sync --locked --python 3.12
    if ($LASTEXITCODE -ne 0) { throw 'Python setup failed.' }
    $sdkVersion = (Get-Content 'global.json' -Raw | ConvertFrom-Json).sdk.version
    if (-not (Test-Path -LiteralPath ".tools\dotnet\sdk\$sdkVersion")) {
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile 'work\dotnet-install.ps1'
        & '.\work\dotnet-install.ps1' -Version $sdkVersion -InstallDir (Join-Path $repoRoot '.tools\dotnet') -NoPath
        if (-not (Test-Path -LiteralPath ".tools\dotnet\sdk\$sdkVersion")) { throw '.NET setup failed.' }
    }
    & '.\.venv\Scripts\python.exe' scripts/download_models.py
    if ($LASTEXITCODE -ne 0) { throw 'Model setup failed.' }
    & '.\scripts\build.ps1' -Test
    Write-Host 'Ready. Double-click Start-Jarvis.cmd.'
}
finally { Pop-Location }
