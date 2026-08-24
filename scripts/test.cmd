@echo off
setlocal
set ROOT=%~dp0..
set SRC=%ROOT%\src
set TESTS=%ROOT%\tests
set BIN=%TESTS%\bin
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REFDIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319

if not exist "%BIN%" mkdir "%BIN%"

set LOCKSRC=
if exist "%SRC%\LockShortcuts.cs" set LOCKSRC="%SRC%\LockShortcuts.cs"

"%CSC%" /nologo /target:exe /platform:x64 /warnaserror+ /utf8output ^
    /reference:"%REFDIR%\System.dll" ^
    /reference:"%REFDIR%\System.Windows.Forms.dll" ^
    /out:"%BIN%\LockShortcutTests.exe" ^
    "%SRC%\NativeMethods.cs" "%SRC%\Config.cs" %LOCKSRC% "%TESTS%\LockShortcutTests.cs"
if errorlevel 1 (
    echo [test] BUILD FAILED
    exit /b 1
)

"%BIN%\LockShortcutTests.exe"
if errorlevel 1 (
    echo [test] FAILED
    exit /b 1
)

echo [test] OK
endlocal
