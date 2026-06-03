@echo off
:: Build a standalone, single-file modmgr_gui.exe at the repo root.
:: No .NET runtime required on the target machine.
:: Usage:  publish.bat            (defaults to win-x64)
::         publish.bat win-arm64
setlocal
set ROOT=%~dp0
set GUI=%ROOT%csgui\MgsvModMgr.Gui
set OUT=%ROOT%publish_tmp
if "%~1"=="" (set RID=win-x64) else (set RID=%~1)

echo Publishing single-file self-contained exe for %RID% ...
dotnet publish "%GUI%" -c Release -r %RID% -p:PublishSingleFile=true -o "%OUT%"
if errorlevel 1 goto fail

copy /Y "%OUT%\modmgr_gui.exe" "%ROOT%modmgr_gui.exe" >nul
rmdir /S /Q "%OUT%"

echo.
echo Done. Distributable exe: %ROOT%modmgr_gui.exe
goto end

:fail
echo PUBLISH FAILED.
exit /b 1

:end
endlocal
