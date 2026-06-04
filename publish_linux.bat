@echo off
:: Build a standalone, single-file modmgr_gui binary for Linux at the repo root.
:: No .NET runtime required on the target machine.
:: Usage:  publish_linux.bat            (defaults to linux-x64)
::         publish_linux.bat linux-arm64
setlocal
set ROOT=%~dp0
set GUI=%ROOT%csgui\MgsvModMgr.Gui
set OUT=%ROOT%publish_tmp_linux
if "%~1"=="" (set RID=linux-x64) else (set RID=%~1)

echo Publishing single-file self-contained binary for %RID% ...
dotnet publish "%GUI%" -c Release -r %RID% --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%OUT%"
if errorlevel 1 goto fail

:: On Linux, the output binary has no file extension (it's not an .exe)
if exist "%OUT%\modmgr_gui" (
    copy /Y "%OUT%\modmgr_gui" "%ROOT%modmgr_gui_%RID%" >nul
) else (
    goto fail
)

rmdir /S /Q "%OUT%"

echo.
echo Done. Distributable Linux binary: %ROOT%modmgr_gui_%RID%
goto end

:fail
echo PUBLISH FAILED.
if exist "%OUT%" rmdir /S /Q "%OUT%"
exit /b 1

:end
endlocal