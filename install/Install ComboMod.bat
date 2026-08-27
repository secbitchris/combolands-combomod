@echo off
REM Double-click entry point. -ExecutionPolicy Bypass applies to this process only and
REM changes nothing system-wide, which is what lets an unsigned script run without
REM asking anyone to loosen their machine settings.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-ComboMod.ps1" %*
echo.
pause
