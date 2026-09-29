@echo off
setlocal
set ROOT=%~dp0..
set SRC=%ROOT%\src
set TESTS=%ROOT%\tests
set BIN=%TESTS%\bin
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REFDIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319

if not exist "%BIN%" mkdir "%BIN%"

"%CSC%" /nologo /target:exe /platform:x64 /warnaserror+ /utf8output ^
    /reference:"%REFDIR%\System.dll" ^
    /reference:"%REFDIR%\System.Windows.Forms.dll" ^
    /out:"%BIN%\BluetoothMonitorPolicyTests.exe" ^
    "%SRC%\BluetoothMonitorPolicy.cs" "%SRC%\NativeMethods.cs" "%SRC%\LockShortcuts.cs" "%TESTS%\BluetoothMonitorPolicyTests.cs"
if errorlevel 1 (
    echo [monitor-policy-test] BUILD FAILED
    exit /b 1
)

"%BIN%\BluetoothMonitorPolicyTests.exe"
if errorlevel 1 (
    echo [monitor-policy-test] FAILED
    exit /b 1
)

echo [monitor-policy-test] OK
endlocal
