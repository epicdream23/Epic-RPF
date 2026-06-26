@echo off
REM Launch the built CodeWalker world editor. Builds first if the exe is missing
REM or looks broken (too small). Shows errors instead of flashing past.
setlocal
set "EXE=%~dp0CodeWalker\bin\Release\net48\CodeWalker.exe"

set "EXESIZE=0"
if exist "%EXE%" for %%F in ("%EXE%") do set "EXESIZE=%%~zF"

if %EXESIZE% LSS 1000000 (
  echo CodeWalker.exe missing or broken - building it first...
  call "%~dp0_build.bat" nopause
  if errorlevel 1 (
    echo.
    echo Build failed - cannot launch. See messages above.
    pause
    exit /b 1
  )
)

start "" "%EXE%"
endlocal
