@echo off
rem Celeste mode uninstaller: starts files\Uninstall-CelesteMode.ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0files\Uninstall-CelesteMode.ps1" %*
pause
