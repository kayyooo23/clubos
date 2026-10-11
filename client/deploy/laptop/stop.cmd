@echo off
rem Stops everything started from this folder.
taskkill /im ClubOS.Overlay.exe /f >nul 2>&1
taskkill /im ClubOS.Service.exe /f >nul 2>&1
echo ClubOS stopped.
timeout /t 2 /nobreak >nul
