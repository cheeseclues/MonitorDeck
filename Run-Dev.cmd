@echo off
setlocal
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Install the .NET 10 SDK first: https://dotnet.microsoft.com/download/dotnet/10.0
  pause
  exit /b 1
)
cd /d "%~dp0MonitorDeck"
dotnet run --project MonitorDeck.csproj
if errorlevel 1 pause
