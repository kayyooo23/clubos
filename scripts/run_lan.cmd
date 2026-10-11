@echo off
rem Starts the ClubOS server for the local network (see run_lan.ps1). Double-click to run.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run_lan.ps1" %*
if errorlevel 1 pause
