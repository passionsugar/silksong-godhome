@echo off
rem The Loom of Recollection - uninstall. Removes BepInEx\plugins\Godhome only; saves, BepInEx and other mods are kept.
setlocal
if not exist "%~dp0install.ps1" goto notextracted
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall %*
echo.
pause
exit /b

:notextracted
echo.
echo Please extract the WHOLE zip to a folder first, then double-click uninstall.bat there.
echo.
pause
exit /b 1
