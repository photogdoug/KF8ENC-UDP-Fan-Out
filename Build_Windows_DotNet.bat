@echo off
setlocal

cd /d "%~dp0"

set "DOTNET_CLI_HOME=%~dp0.dotnet-cli"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"

where dotnet.exe >nul 2>nul
if errorlevel 1 (
  echo Could not find the .NET SDK.
  echo Install the .NET 8 SDK from https://dotnet.microsoft.com/download
  exit /b 1
)

dotnet publish WsjtxUdpFanout.csproj ^
  --configuration Release ^
  --runtime win-x64 ^
  --self-contained true ^
  --output publish ^
  --configfile NuGet.Config ^
  -p:PublishSingleFile=true

if errorlevel 1 exit /b 1

echo.
echo Built publish\WsjtxUdpFanout.exe
exit /b 0
