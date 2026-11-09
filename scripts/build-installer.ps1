param([string]$InnoCompiler, [switch]$SkipChecks)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot
$env:DOTNET_CLI_HOME=Join-Path $repoRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES=Join-Path $repoRoot '.tools\nuget'
$env:APPDATA=Join-Path $repoRoot '.tools\build-appdata'
$env:DOTNET_ROOT=Join-Path $repoRoot '.tools\dotnet'
$dotnet=Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
$python=Join-Path $repoRoot '.venv\Scripts\python.exe'
if(-not $InnoCompiler) { $InnoCompiler=Join-Path $repoRoot '.tools\inno\ISCC.exe' }
if(-not(Test-Path -LiteralPath $InnoCompiler)) { throw 'Install Inno Setup 6.7.4 or pass -InnoCompiler.' }
if(-not $SkipChecks) {
 & $dotnet restore Jarvis.slnx --configfile NuGet.Config --locked-mode
 if($LASTEXITCODE -ne 0){throw "Restore failed"}
 & $dotnet test Jarvis.slnx --no-restore -m:1 -c Release -p:UseSharedCompilation=false
 if($LASTEXITCODE -ne 0){throw 'Tests failed'}
 & $python -m pytest -q
 if($LASTEXITCODE -ne 0){throw 'Speech tests failed'}
}
$build=Join-Path $repoRoot ('artifacts\installer-build-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$payload=Join-Path $build 'payload'
$release=Join-Path $repoRoot 'artifacts\release'
& $dotnet publish src/Jarvis.App/Jarvis.App.csproj --configfile NuGet.Config -c Release -r win-x64 --self-contained true -o $payload -p:UseSharedCompilation=false -p:NuGetLockFilePath=packages.win-x64.lock.json -p:RestoreLockedMode=true
if($LASTEXITCODE -ne 0){throw 'Publish failed'}
& $python scripts/prepare_installer.py $payload
if($LASTEXITCODE -ne 0){throw 'Payload preparation failed'}
$report=Join-Path $build 'check.json'
$probe=Start-Process -FilePath (Join-Path $payload 'Jarvis.exe') -ArgumentList '--check-installation',('"'+$report+'"') -WindowStyle Hidden -PassThru
if(-not $probe.WaitForExit(180000)){ $probe.Kill(); throw 'Package check timed out' }
if($probe.ExitCode -ne 0 -or -not(Test-Path -LiteralPath $report)){throw 'Package check failed'}
if(-not (Get-Content -LiteralPath $report -Raw | ConvertFrom-Json).success){throw 'Models or voice failed'}
& (Join-Path $PSScriptRoot 'clear-release.ps1')
& $InnoCompiler ('/DPayloadDir='+$payload) ('/DReleaseDir='+$release) installer/Jarvis.iss
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}
$file=Join-Path $release 'JARVIS-Setup-0.15.1-win-x64.exe'
$hash=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($file+'.sha256', $hash+'  '+[IO.Path]::GetFileName($file)+[Environment]::NewLine)
Copy-Item -LiteralPath (Join-Path $repoRoot 'installer\README-RU.txt') -Destination (Join-Path $release 'README-RU.txt') -Force
Write-Output $file
