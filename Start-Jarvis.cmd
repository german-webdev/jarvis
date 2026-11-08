@echo off
setlocal
set "DOTNET_ROOT=%~dp0.tools\dotnet"
set "DOTNET_ROOT_X64=%~dp0.tools\dotnet"
set "JARVIS_APP=%~dp0artifacts\prototype\Jarvis.exe"
if not exist "%DOTNET_ROOT%\dotnet.exe" goto missing
if not exist "%JARVIS_APP%" goto missing
if not exist "%~dp0artifacts\prototype\Jarvis.dll" goto missing
if not exist "%~dp0.venv\Scripts\python.exe" goto missing
start "" /d "%~dp0" "%JARVIS_APP%"
if errorlevel 1 goto failed
exit /b 0

:missing
echo JARVIS: required application files are missing.
echo Run scripts\setup.ps1 as described in README.md.
echo Project folder: %~dp0
pause
exit /b 1

:failed
echo JARVIS could not start. Please report the error shown above.
pause
exit /b 1
