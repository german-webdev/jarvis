# Optional PowerShell entry point. The normal launcher does not require PowerShell.
$repoRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $repoRoot 'Start-Jarvis.cmd')
exit $LASTEXITCODE
