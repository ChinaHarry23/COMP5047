EDGE circuit self-test (Windows)
1. Install Python 3 from python.org (tick "Add python.exe to PATH").
2. If no COM port shows up, install the CH340 USB driver:
   https://www.wch-ic.com/downloads/CH341SER_EXE.html
3. Plug in the FireBeetle, double-click test_circuit.bat, type the COM port.
4. Watch for PASS/FAIL. When asked: cover the light sensor, press the button,
   and confirm you saw the LED blink. Type r + Enter to rerun after a fix.
Tests: I2C scan, MPU-6050 (~1 g when still), BH1750 (lux drops when covered),
LED on D3/GPIO26, button on D2/GPIO25 to GND.
Afterwards Bao reflashes the real game firmware (pio run -e tag -t upload).
