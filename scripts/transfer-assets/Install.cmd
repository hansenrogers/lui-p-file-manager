@echo off
setlocal
cd /d "%~dp0"

echo Installing Lui-P File Manager...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-LuiPFileManager.ps1" -CreateDesktopShortcut -LaunchAfterInstall
set ERR=%ERRORLEVEL%

if not "%ERR%"=="0" (
  echo.
  echo Install failed with exit code %ERR%.
  pause
  exit /b %ERR%
)

echo.
echo Install completed successfully.
pause
exit /b 0
