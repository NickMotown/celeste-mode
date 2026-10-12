@echo off
rem Celeste mode installer: starts files\Install-CelesteMode.ps1 (PowerShell
rem refuses a downloaded script started by a double-click).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0files\Install-CelesteMode.ps1" %*
pause
