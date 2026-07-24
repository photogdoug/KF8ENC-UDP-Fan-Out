# WSJT-X UDP Fanout

Windows UDP relay for WSJT-X companion programs.

Default flow:

```text
WSJT-X -> 127.0.0.1:2236 -> WsjtxUdpFanout.exe
                              -> GridTracker       127.0.0.1:2237
                              -> Logger            127.0.0.1:2238
                              -> WRL CAT Control   127.0.0.1:2239
```

The relay is bidirectional by default. It learns the WSJT-X UDP source socket from WSJT-X traffic, then relays companion-app command packets back to WSJT-X.

## WSJT-X Setup

In WSJT-X:

```text
File -> Settings -> Reporting
UDP Server address: 127.0.0.1
UDP Server port:    2236
Accept UDP requests: checked
```

`Accept UDP requests` is required if a companion app sends commands back to WSJT-X.

## Live Destination Commands

Type commands directly in the program window:

```text
add JTSync 2249
add "GridTracker" 127.0.0.1:2238
set JTSync 127.0.0.1:2249
remove JTSync
rename JTSync "JT Sync"
save
load
config
bidirectional
read-only
refresh 500
clearstats
quit
```

Changes are saved to:

```text
%APPDATA%\WsjtxUdpFanout\WsjtxUdpFanout.ini
```

## Build

Install the .NET 8 SDK, then run:

```bat
Build_Windows_DotNet.bat
```

The direct .NET command is:

```bat
dotnet publish WsjtxUdpFanout.csproj --configuration Release --runtime win-x64 --self-contained true --output publish -p:PublishSingleFile=true
```

This creates a self-contained, single-file executable at:

```text
publish\WsjtxUdpFanout.exe
```

The destination computer does not need the .NET runtime installed.

## Installer

Install the .NET 8 SDK and Inno Setup 6, then run:

```bat
Build_Installer_Inno.bat
```

The installer will be written to:

```text
installer\WsjtxUdpFanoutSetup.exe
```
