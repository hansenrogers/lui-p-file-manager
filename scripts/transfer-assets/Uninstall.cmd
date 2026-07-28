@echo off
setlocal
cd /d "%~dp0"

echo Uninstalling Lui-P File Manager...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-LuiPFileManager.ps1" -ForceCloseRunningApp
set ERR=%ERRORLEVEL%

if not "%ERR%"=="0" (
  echo.
  echo Uninstall failed with exit code %ERR%.
  pause
  exit /b %ERR%
)

echo.
echo Uninstall completed successfully.
pause
exit /b 0
