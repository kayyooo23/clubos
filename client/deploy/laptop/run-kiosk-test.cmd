@echo off
rem REAL lock mode: the lock screen covers the whole screen. Test it only after run-windowed.cmd works.
cd /d "%~dp0"
echo.
echo  This will cover the WHOLE screen with the ClubOS lock screen
echo  and block Win, Alt+Tab, Alt+F4 until a guest session starts.
echo.
echo  HOW TO EXIT: press Ctrl+Shift+F12 three times in a row, quickly.
echo  Spare exit: Ctrl+Alt+Del - Task Manager - end ClubOS.Overlay.exe, then run stop.cmd
echo.
echo  Press any key to continue, or close this window to cancel.
pause >nul
set CLUBOS_CONFIG=%~dp0agent.json
set CLUBOS_TOKEN_FILE=%~dp0token.bin
taskkill /im ClubOS.Overlay.exe /f >nul 2>&1
taskkill /im ClubOS.Service.exe /f >nul 2>&1
start "ClubOS service" /min "%~dp0Service\ClubOS.Service.exe"
timeout /t 2 /nobreak >nul
start "" /wait "%~dp0Overlay\ClubOS.Overlay.exe" --kiosk --allow-exit
taskkill /im ClubOS.Service.exe /f >nul 2>&1
