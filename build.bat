@echo off
setlocal

:: mgsv_modmgr build script. Builds two exes from shared core.cpp:
::   modmgr.exe      (console, CLI)
::   modmgr_gui.exe  (windows subsystem, GUI)
set VCVARS="C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat"
set SCRIPT_DIR=%~dp0
set SRC_CORE=%SCRIPT_DIR%src\core.cpp
set SRC_CLI=%SCRIPT_DIR%src\cli.cpp
set SRC_GUI=%SCRIPT_DIR%src\gui.cpp

set OUT_CLI=%SCRIPT_DIR%modmgr.exe
set OUT_GUI=%SCRIPT_DIR%modmgr_gui.exe

if not exist %VCVARS% (
    echo ERROR: vcvarsall.bat not found at %VCVARS%
    exit /b 1
)

call %VCVARS% x64 >nul

set CFLAGS=/nologo /O2 /MT /EHsc /W3 /std:c++20 /permissive- /I"%SCRIPT_DIR%src"

echo [1/2] Compiling CLI ...
cl.exe %CFLAGS% /Fe%OUT_CLI% %SRC_CORE% %SRC_CLI% /link /SUBSYSTEM:CONSOLE
if errorlevel 1 goto fail

echo [2/2] Compiling GUI ...
cl.exe %CFLAGS% /Fe%OUT_GUI% %SRC_CORE% %SRC_GUI% /link /SUBSYSTEM:WINDOWS user32.lib gdi32.lib comctl32.lib shell32.lib ole32.lib
if errorlevel 1 goto fail

del /q %SCRIPT_DIR%*.obj 2>nul
echo.
echo Build succeeded:
echo   %OUT_CLI%
echo   %OUT_GUI%
goto end

:fail
echo BUILD FAILED.
exit /b 1

:end
endlocal
