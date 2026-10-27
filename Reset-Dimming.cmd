@echo off
setlocal
echo Closing MonitorDeck and clearing saved software dimming for this Windows user...
taskkill /F /IM MonitorDeck.exe >nul 2>nul
if exist "%LOCALAPPDATA%\MonitorDeck\software-dimming.json" del /F /Q "%LOCALAPPDATA%\MonitorDeck\software-dimming.json"
echo Software-dimming preferences reset. Restart MonitorDeck when ready.
pause
