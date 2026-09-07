@echo off
setlocal EnableDelayedExpansion

rem ---------------------------------------------------------------------------
rem Legion Fan Tray - release build
rem
rem   build.bat              self-contained single .exe (no .NET install needed)
rem   build.bat framework    small .exe, needs the .NET 8 Desktop Runtime
rem   build.bat clean        wipe dist\, bin\ and obj\ and stop
rem
rem Output lands in dist\ next to this file.
rem ---------------------------------------------------------------------------

set "ROOT=%~dp0"
set "PROJ=%ROOT%src\LegionFanTray.csproj"
set "DIST=%ROOT%dist"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [X] dotnet was not found on PATH. Install the .NET 8 SDK:
    echo     https://dotnet.microsoft.com/download/dotnet/8.0
    exit /b 1
)

if /i "%~1"=="clean" (
    echo Cleaning...
    if exist "%DIST%" rd /s /q "%DIST%"
    if exist "%ROOT%src\bin" rd /s /q "%ROOT%src\bin"
    if exist "%ROOT%src\obj" rd /s /q "%ROOT%src\obj"
    echo Done.
    exit /b 0
)

rem A running copy holds a lock on the output and the build fails with MSB3027,
rem so stop it first rather than making the user work out why the copy failed.
tasklist /fi "IMAGENAME eq LegionFanTray.exe" 2>nul | find /i "LegionFanTray.exe" >nul
if not errorlevel 1 (
    echo Stopping the running LegionFanTray.exe...
    taskkill /im LegionFanTray.exe /f >nul 2>&1
    rem Give Windows a moment to release the file handles.
    ping -n 2 127.0.0.1 >nul
)

set "MODE=self-contained"
set "HOSTARGS=--self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true"
if /i "%~1"=="framework" (
    set "MODE=framework-dependent"
    set "HOSTARGS=--self-contained false -p:PublishSingleFile=true"
)

echo.
echo Building Legion Fan Tray  [%MODE%, win-x64, Release]
echo.

if exist "%DIST%" rd /s /q "%DIST%"

dotnet publish "%PROJ%" -c Release -r win-x64 %HOSTARGS% -o "%DIST%"
if errorlevel 1 (
    echo.
    echo [X] Build failed.
    exit /b 1
)

rem Debug symbols are not wanted in a drop that gets copied to another machine.
if exist "%DIST%\*.pdb" del /q "%DIST%\*.pdb"

echo.
if not exist "%DIST%\LegionFanTray.exe" (
    echo [X] Build reported success but dist\LegionFanTray.exe is missing.
    exit /b 1
)

for %%F in ("%DIST%\LegionFanTray.exe") do set /a SIZE=%%~zF/1048576
echo [OK] dist\LegionFanTray.exe  (!SIZE! MB)
if /i "%MODE%"=="framework-dependent" echo      Needs the .NET 8 Desktop Runtime on the target machine.
echo      Run it as administrator - the app needs EC access for fan control.
echo.
endlocal
