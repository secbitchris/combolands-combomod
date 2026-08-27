@echo off
REM Double-click entry point for removal. See Install ComboMod.bat for why Bypass.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-ComboMod.ps1" %*
echo.
pause
