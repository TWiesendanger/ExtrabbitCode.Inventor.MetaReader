@echo off
REM Regenerates the documentation screenshots (light + dark) from the WinUI viewer.
REM Builds InventorMeta.App, then runs it headless in --shoot-docs mode against the bundled
REM sample files, writing one PNG per view into documentation/public/images/app.
REM Run from anywhere; paths are resolved relative to this script.
setlocal
set SCRIPT_DIR=%~dp0
set REPO=%SCRIPT_DIR%..\..
set APP=%REPO%\src\InventorMeta.App
set OUT=%SCRIPT_DIR%..\public\images\app
set SAMPLES=%APP%\Assets\SampleFiles

dotnet build "%APP%" -c Release -r win-x64 || exit /b 1

set EXE=%APP%\bin\Release\net10.0-windows10.0.19041.0\win-x64\InventorMeta.App.exe
echo Shooting docs screenshots into "%OUT%" ...
"%EXE%" --shoot-docs "%OUT%" --samples "%SAMPLES%"
echo Done.
endlocal
