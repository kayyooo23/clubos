@echo off
rem Safe test: service + lock screen in a normal window. Nothing is installed, nothing is blocked.
cd /d "%~dp0"
set CLUBOS_CONFIG=%~dp0agent.json
set CLUBOS_TOKEN_FILE=%~dp0token.bin
taskkill /im ClubOS.Overlay.exe /f >nul 2>&1
taskkill /im ClubOS.Service.exe /f >nul 2>&1
start "ClubOS service" /min "%~dp0Service\ClubOS.Service.exe"
timeout /t 2 /nobreak >nul
start "" /wait "%~dp0Overlay\ClubOS.Overlay.exe"
taskkill /im ClubOS.Service.exe /f >nul 2>&1
