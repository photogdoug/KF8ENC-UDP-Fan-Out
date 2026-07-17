@echo off
setlocal

cd /d "%~dp0"

where cl.exe >nul 2>nul
if %errorlevel%==0 goto build

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
  for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%i"
)

if defined VSINSTALL (
  if exist "%VSINSTALL%\Common7\Tools\VsDevCmd.bat" (
    call "%VSINSTALL%\Common7\Tools\VsDevCmd.bat" -arch=x64
    goto build
  )
)

echo Could not find cl.exe.
echo Install Visual Studio Build Tools with the Desktop development with C++ workload.
exit /b 1

:build
cl /EHsc /std:c++17 /W4 WsjtxUdpFanout.cpp ws2_32.lib /Fe:WsjtxUdpFanout.exe
exit /b %errorlevel%
