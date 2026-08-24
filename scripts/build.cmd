@echo off
setlocal
set ROOT=%~dp0..
set BUILD=%ROOT%\build
set SRC=%ROOT%\src
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REFDIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319

if not exist "%BUILD%" mkdir "%BUILD%"

set WINMD=%WINDIR%\System32\WinMetadata
set WRTDIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set FACADES=%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\Facades
if not exist "%FACADES%\System.Runtime.dll" set FACADES=%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\Facades
set SDKREFS=%ProgramFiles(x86)%\Windows Kits\10\References\10.0.22621.0
if not exist "%SDKREFS%" for /D %%i in ("%ProgramFiles(x86)%\Windows Kits\10\References\10.*") do set SDKREFS=%%i

set WINMD_REF=%ProgramFiles(x86)%\Windows Kits\10\UnionMetadata\10.0.22621.0\Windows.winmd
if not exist "%WINMD_REF%" (
  for /f "delims=" %%i in ('dir /s /b "%ProgramFiles(x86)%\Windows Kits\10\UnionMetadata\Windows.winmd" 2^>nul') do set WINMD_REF=%%i & goto :winmd_found
  echo [build] WARNING: Windows.winmd not found at expected path, build may fail
  set WINMD_REF=
)
:winmd_found

"%CSC%" /nologo /target:winexe /platform:x64 /warnaserror+ /nowarn:1701,1702 /utf8output /optimize+ /debug:pdbonly ^
    /reference:"%REFDIR%\System.dll" ^
    /reference:"%REFDIR%\System.Drawing.dll" ^
    /reference:"%REFDIR%\System.Windows.Forms.dll" ^
    /reference:"%WRTDIR%\System.Runtime.WindowsRuntime.dll" ^
    /reference:"%FACADES%\System.Runtime.dll" ^
    /reference:"%FACADES%\System.Runtime.InteropServices.WindowsRuntime.dll" ^
    /reference:"%FACADES%\System.ObjectModel.dll" ^
    /reference:"%FACADES%\System.Threading.Tasks.dll" ^
    /reference:"%WINMD_REF%" ^
    /out:"%BUILD%\BluetoothAutoLock.exe" ^
    "%SRC%\NativeMethods.cs" "%SRC%\LockShortcuts.cs" "%SRC%\Config.cs" "%SRC%\Logger.cs" "%SRC%\WinRtBluetooth.cs" "%SRC%\BluetoothMonitor.cs" "%SRC%\LolOptimizer.cs" "%SRC%\GameEnvironmentOptimizer.cs" "%SRC%\SettingsForm.cs" "%SRC%\TrayApp.cs" "%SRC%\Program.cs"
if errorlevel 1 (
    echo [build] FAILED
    exit /b 1
)

if exist "%ROOT%\config\config.ini" if not exist "%BUILD%\config.ini" copy /y "%ROOT%\config\config.ini" "%BUILD%\config.ini" >nul

echo [build] OK -^> %BUILD%\BluetoothAutoLock.exe
endlocal
