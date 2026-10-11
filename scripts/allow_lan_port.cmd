@echo off
rem Opens TCP port 8000 in Windows Firewall ONLY for computers from your own subnet (for testing a second PC).
rem Run as Administrator: right click this file - "Run as administrator".
net session >nul 2>&1
if errorlevel 1 (
  echo This file must be run as Administrator: right click - Run as administrator.
  pause
  exit /b 1
)
netsh advfirewall firewall delete rule name="ClubOS dev server (LAN)" >nul 2>&1
netsh advfirewall firewall add rule name="ClubOS dev server (LAN)" dir=in action=allow protocol=TCP localport=8000 remoteip=localsubnet profile=any
echo.
echo Done. Port 8000 is open for your local subnet only.
echo To close it later run remove_lan_port.cmd as Administrator.
pause
