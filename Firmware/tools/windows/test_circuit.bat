@echo off
REM EDGE circuit self-test for Windows. Double-click me.
REM Needs Python 3 (python.org, tick "Add python.exe to PATH").
setlocal
cd /d "%~dp0"
where python >nul 2>nul || (echo [!] Python not found. Install from https://www.python.org/downloads/ and tick "Add to PATH". & pause & exit /b 1)
echo Installing/updating esptool + pyserial (first time only)...
python -m pip install --quiet --upgrade esptool pyserial
echo.
echo Plug the FireBeetle in by USB-C. Ports found:
python -m serial.tools.list_ports -v
echo.
set /p PORT=Type the COM port (e.g. COM3) and press Enter: 
echo.
echo Flashing self-test firmware to %PORT% ...
python -m esptool --chip esp32 --port %PORT% --baud 921600 write_flash 0x0 edge_selftest.bin
if errorlevel 1 (echo [!] Flash failed. Try another cable/port, or hold BOOT while it says Connecting. & pause & exit /b 1)
echo.
echo Opening serial monitor. Follow the prompts (cover sensor, press button).
echo Type r then Enter to rerun. Press Ctrl+] to quit.
timeout /t 2 >nul
python -m serial.tools.miniterm %PORT% 115200 --eol LF
pause
