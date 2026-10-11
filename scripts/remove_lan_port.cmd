@echo off
rem Removes the firewall rule created by allow_lan_port.cmd. Run as Administrator.
net session >nul 2>&1
if errorlevel 1 (
  echo This file must be run as Administrator: right click - Run as administrator.
  pause
  exit /b 1
)
netsh advfirewall firewall delete rule name="ClubOS dev server (LAN)"
echo.
echo Done.
pause
