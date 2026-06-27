@echo off
REM ==========================================================================
REM  Epic RPF - build the cloned CodeWalker world editor.
REM  Builds the CodeWalker (world editor) project in Release and stages the
REM  runtime Shaders\ + icons\ folders next to the exe.
REM  Pass "nopause" as the first arg to skip the final pause (used by Run.bat).
REM ==========================================================================
setlocal
cd /d "%~dp0"

set "MSBUILD="
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do set "MSBUILD=%%i"
if not defined MSBUILD set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"

echo Using MSBuild: %MSBUILD%
"%MSBUILD%" "CodeWalker\CodeWalker.csproj" -t:Build -p:Configuration=Release -restore -m -v:m
if errorlevel 1 goto :failed

set "OUT=CodeWalker\bin\Release\net48"
echo Staging Shaders and icons into %OUT% ...
xcopy /E /I /Y /Q "Shaders" "%OUT%\Shaders" >nul
xcopy /E /I /Y /Q "icons"   "%OUT%\icons"   >nul

REM Validate the produced exe is a real executable (must be large; a few KB = broken).
set "EXE=%OUT%\CodeWalker.exe"
if not exist "%EXE%" goto :failed
for %%F in ("%EXE%") do set "EXESIZE=%%~zF"
if %EXESIZE% LSS 1000000 (
  echo.
  echo ERROR: "%EXE%" is only %EXESIZE% bytes - build output looks broken.
  goto :failed
)

echo.
echo Build OK. Output exe: "%EXE%"  [%EXESIZE% bytes]
if /i "%~1"=="nopause" goto :eof
pause
goto :eof

:failed
echo.
echo BUILD FAILED.
if /i "%~1"=="nopause" exit /b 1
pause
exit /b 1
