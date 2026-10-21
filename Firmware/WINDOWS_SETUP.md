# EDGE firmware on Windows: setup and circuit test

For any teammate on Windows who needs to flash the FireBeetle 2 ESP32-E and check the soldered circuit.

## 1. Install (once)
1. **Git**: https://git-scm.com/download/win (default options are fine).
2. **VS Code**: https://code.visualstudio.com/
3. In VS Code, open Extensions (`Ctrl+Shift+X`), search **PlatformIO IDE**, install, and restart VS Code.
4. **USB driver** (only if the board doesn't appear as a COM port in step 4):
   CH340 driver from https://www.wch-ic.com/downloads/CH341SER_EXE.html
   Use a USB-C **data** cable; some cables are charge-only.

## 2. Get the code
Open **PowerShell** (Start menu, type "PowerShell") and run:
```
cd $HOME\Documents
git clone --filter=blob:none --sparse https://github.com/ChinaHarry23/COMP5047.git
cd COMP5047
git sparse-checkout set Firmware
```
This downloads only the `Firmware` folder, not the large Unity project.
To get updates later: `cd $HOME\Documents\COMP5047` then `git pull`.

## 3. Open the project
VS Code > **File > Open Folder** > `Documents\COMP5047\Firmware`.
The first time, PlatformIO downloads the ESP32 toolchain (a few minutes; watch the bottom status bar).

## 4. Check the board is connected
Plug in the FireBeetle. Open the PlatformIO terminal:
`Ctrl+Shift+P` > **PlatformIO: New Terminal**, then run:
```
pio device list
```
You should see a COM port such as `COM3`. If nothing appears, install the CH340 driver or try another cable/USB port.

## 5. Run the circuit self-test
```
pio run -e selftest -t upload
pio device monitor -e selftest
```
Follow the prompts in the monitor:

| Step | Test | What you do |
|---|---|---|
| 1 | I2C scan (SDA GPIO21, SCL GPIO22) | nothing |
| 2 | MPU-6050 found, reads about 1 g | keep the board still and flat |
| 3 | BH1750 found, lux drops when covered | cover the sensor with a finger for 5 s |
| 4 | LED on D3 (GPIO26) blinks 3x then fades | watch it (not auto-checked) |
| 5 | Button on D2 (GPIO25) | press it within 10 s |

It ends with `RESULT: x PASS, y FAIL`, then a live view (`button=... lux=... g=...`).
Pressing the button also lights the LED.
- Rerun after fixing something: type `r` and press Enter.
- Quit the monitor: `Ctrl+C`.

## 6. Fixing FAILs

| FAIL | Likely cause |
|---|---|
| No I2C devices | 3V3 or GND not reaching the sensors; SDA/SCL swapped; cold solder joint |
| MPU-6050 not found | MPU VCC/GND/SDA/SCL joint; expected address 0x68 (0x69 if AD0 is tied to 3V3) |
| MPU not ~1 g | loose joint, or board moving during the test |
| BH1750 not found | BH1750 VCC/GND/SDA/SCL joint; expected address 0x23 (0x5C if ADDR is high) |
| Lux doesn't drop | sensor blocked or upside down, or the room is already dark |
| LED doesn't blink | LED backwards (long leg goes to the resistor/D3 side, short leg to GND) or open resistor joint |
| Button reads pressed when untouched | button shorted to GND, or wrong legs; use diagonally opposite legs |
| Button press not seen | button not wired between D2 and GND |

Wiring reference: D3 (GPIO26) > 330 ohm > LED > GND. Button between D2 (GPIO25) and GND (the internal pull-up is used; an optional 10k from 3V3 to D2 is fine).

## 7. Flash the real game firmware
1. Copy `include\secrets.h.example` to `include\secrets.h` and fill in the Wi-Fi details (ask Bao privately). **Never commit `secrets.h`**; it is git-ignored.
2. Run:
```
pio run -e tag -t upload
pio device monitor -e tag
```

## Common Windows problems
- **"Access is denied" / port busy on upload**: close the serial monitor (and Arduino IDE or any other serial app) first.
- **Wrong board picked**: add the port by hand, e.g. `pio run -e selftest -t upload --upload-port COM3` and `pio device monitor -e selftest -p COM3`.
- **Stuck on "Connecting..."**: hold the **BOOT** button on the FireBeetle until uploading starts.
- **`pio` not recognised**: use the PlatformIO terminal from step 4, not a normal PowerShell window.
- **Garbled text in the monitor**: the speed must be 115200 (already set in `platformio.ini`).

Alternative without PlatformIO: `tools\windows\test_circuit.bat` (needs Python 3) flashes the prebuilt self-test and opens a monitor.
