@echo off
setlocal

cd /d "%~dp0"

if not exist WsjtxUdpFanout.exe (
  call Build_Windows_MSVC.bat
  if errorlevel 1 exit /b 1
)

set "ISCC="
where ISCC.exe >nul 2>nul
if %errorlevel%==0 set "ISCC=ISCC.exe"

if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"

if not defined ISCC (
  echo Could not find Inno Setup Compiler ISCC.exe.
  echo Install Inno Setup 6 or add ISCC.exe to PATH.
  exit /b 1
)

"%ISCC%" WsjtxUdpFanout.iss
exit /b %errorlevel%
