$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Join-Path $repoRoot '.tools\dotnet'
$dotnetExe = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$appDll = Join-Path $repoRoot 'artifacts\prototype\Jarvis.dll'
if (-not (Test-Path -LiteralPath $appDll) -or -not (Test-Path -LiteralPath $dotnetExe)) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show('First run scripts\setup.ps1. See README.md.', 'JARVIS') | Out-Null
    exit 1
}
# Hide the console host; the WPF application creates its own visible window.
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $dotnetExe
$startInfo.Arguments = '"' + $appDll + '"'
$startInfo.WorkingDirectory = $repoRoot
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
[System.Diagnostics.Process]::Start($startInfo) | Out-Null
