$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path $PSScriptRoot -Parent
$executable = Join-Path $projectDirectory 'artifacts\prototype\Jarvis.exe'
if (!(Test-Path -LiteralPath $executable)) { throw 'Build JARVIS before creating the shortcut.' }
$desktopDirectory = [Environment]::GetFolderPath('DesktopDirectory')
$shortcutPath = Join-Path $desktopDirectory 'JARVIS.lnk'
$shellObject = New-Object -ComObject WScript.Shell
try {
    if (Test-Path -LiteralPath $shortcutPath) {
        $existing = $shellObject.CreateShortcut($shortcutPath)
        try {
            if ($existing.TargetPath -ne $executable) { throw 'A shortcut named JARVIS already points to a different application. It was not changed.' }
        }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($existing) }
    }
    $shortcut = $shellObject.CreateShortcut($shortcutPath)
    try {
        $shortcut.TargetPath = $executable
        $shortcut.WorkingDirectory = $projectDirectory
        $shortcut.IconLocation = "$executable,0"
        $shortcut.Description = 'JARVIS — локальный голосовой помощник'
        $shortcut.Save()
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
    $saved = $shellObject.CreateShortcut($shortcutPath)
    try {
        if ($saved.TargetPath -ne $executable) { throw 'Shortcut verification failed.' }
        Write-Output $shortcutPath
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($saved) }
}
finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shellObject) }
