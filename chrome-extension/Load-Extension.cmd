@echo off
setlocal
cd /d "%~dp0"

echo Rebuilding the Live Captions host and preparing the Chrome sidebar...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0native-host\Install-NativeHost.ps1"
if errorlevel 1 (
  echo.
  echo Host install failed. Make sure the .NET 8 SDK is installed, then run this file again.
  pause
  exit /b 1
)

echo %CD%| clip
start "" "chrome://extensions"
echo.
echo Path copied to the clipboard:
echo   %CD%
echo.
echo In Chrome:
echo   1. Turn on Developer mode (top right)
echo   2. Click Load unpacked
echo   3. Paste the path and select this folder
echo   4. Confirm the ID is nkcgnofkagfllgbfompiejcdadpbbikp
echo   5. Click the extension icon to open the sidebar
echo   6. Press Win+Ctrl+L, then click Start
echo.
pause
