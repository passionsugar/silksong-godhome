@echo off
rem The Loom of Recollection - one-click installer. Double-click this file after extracting the whole zip.
rem It runs install.ps1 next to it. Extra arguments are passed through (e.g. -GameDir "D:\Games\Hollow Knight Silksong").
setlocal
if not exist "%~dp0install.ps1" goto notextracted
if not exist "%~dp0BepInEx\plugins\Godhome\Godhome.dll" goto notextracted
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
exit /b

:notextracted
echo.
echo Please extract the WHOLE zip to a folder first, then double-click install.bat there.
echo Qing xian ba zheng ge ya suo bao jie ya chu lai, zai shuang ji li mian de install.bat.
echo.
pause
exit /b 1
