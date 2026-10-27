@echo off
setlocal
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo ERROR: .NET SDK is not installed.
  echo Install the .NET 10 SDK for Windows x64 from:
  echo https://dotnet.microsoft.com/download/dotnet/10.0
  echo Then run this file again.
  echo.
  pause
  exit /b 1
)
cd /d "%~dp0MonitorDeck"
echo Publishing MonitorDeck as a standalone Windows x64 application...
dotnet publish MonitorDeck.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%~dp0release"
if errorlevel 1 (
  echo.
  echo Build failed. Copy the error text and share it for troubleshooting.
  pause
  exit /b 1
)
echo.
echo SUCCESS: %~dp0release\MonitorDeck.exe
echo The executable does not require a .NET runtime on the target machine.
echo.
pause
