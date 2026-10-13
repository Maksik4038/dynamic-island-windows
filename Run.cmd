@echo off
rem ===================================================================
rem  Dynamic Island - run it right now, without installing anything.
rem  Double-click this file, or run it from a terminal.
rem
rem  The executable is not stored in the repository (.gitignore keeps
rem  build output out), so if it is missing this script builds it first.
rem ===================================================================
setlocal
cd /d "%~dp0"

if not exist "DynamicIsland.exe" (
  echo.
  echo   DynamicIsland.exe not found - building from source...
  echo   Needs the .NET Framework 4.x and the Windows SDK 10.
  echo.
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build.ps1"
  if errorlevel 1 (
    echo.
    echo   Build failed. Install the Windows SDK 10 and try again, or run:
    echo       powershell -ExecutionPolicy Bypass -File Build.ps1
    echo.
    pause
    exit /b 1
  )
)

echo.
echo   Starting Dynamic Island...
echo   Look at the top centre of your screen.
echo   Right-click the tray icon to quit.
echo.

start "" "%~dp0DynamicIsland.exe"

rem give it a moment, then confirm it is actually alive
timeout /t 2 /nobreak >nul
tasklist /fi "imagename eq DynamicIsland.exe" | find /i "DynamicIsland.exe" >nul
if errorlevel 1 (
  echo   It did not stay running. Run DynamicIsland.exe --selftest
  echo   from a terminal to see what is missing on this machine.
  echo.
  pause
  exit /b 1
)

echo   Running. This window will close.
timeout /t 2 /nobreak >nul
exit /b 0
