# EDGE tag as bat controller — setup (one page)

**Path:** MPU-6050 → ESP32 (`rev3/firmware/src/tag`, 100 Hz complementary filter) → Wi-Fi UDP
`ORI roll pitch yaw gx gy gz btn seq` → Unity `UdpLink` (port 1234) → `EdgeTagInput`
(calibrate, remap, filter, loss handling) → `EdgeTag` Input System device → `EdgeControls`
`Fly/Steer`, `Fly/Climb` → `BatFlightController`. Keyboard (WASD/arrows) and gamepad drive the same actions.

## 1. Wiring check
See `Final Project/docs/CIRCUIT_GUIDE_v1.md`.

| Signal | Board label | GPIO |
|---|---|---|
| MPU-6050 + BH1750 SDA / SCL | SDA / SCL | 21 / 22 |
| MPU-6050 + BH1750 VCC / GND | 3V3 / GND | — |
| Button (short = start/stop stream, long ≥1 s = zero) → GND | D2 | 25 |
| LED (VIB brightness, motor stand-in) via 330 Ω → GND | D3 | 26 |

Packets: `ORI ...` 100 Hz + `LUX <lx>` 10 Hz while streaming; `HELLO EDGE-TAG paused <seq>` 1 Hz when paused.
Multimeter: no 3V3–GND short before power. I²C scan should show 0x23 + 0x68.

## 2. Flash firmware
1. `cp include/secrets.h.example include/secrets.h` (git-ignored); set `EDGE_WIFI_SSID/PASS`
   (phone hotspot, 2.4 GHz, iPhone "Maximize Compatibility"). Optionally `EDGE_UNITY_HOST` = Mac IP;
   otherwise the tag broadcasts to 255.255.255.255.
2. `cd "Final Project/rev3/firmware" && ~/.platformio/penv/bin/pio run -e tag -t upload`
3. `pio device monitor -e tag` → I²C scan shows 0x68; keep the tag still ~1 s for gyro cal;
   lines `r= p= y= ... wifi=1 sent=...` mean it is streaming.

## 3. Wi-Fi / IP
Mac and tag on the **same hotspot**. Mac IP: `ipconfig getifaddr en0`. macOS firewall: allow Unity
incoming connections (prompt on first Play). Check packets reach the Mac:
`sudo tcpdump -n -i en0 udp port 1234` (Unity must not need to be running).

## 4. Calibrate (in Play mode)
Hold the tag in its neutral "flying level" pose → press **Z** (or long-press D2 on the tag, or
gamepad Select). It also auto-zeroes on the first packet. (**C** is the camera toggle.)
HUD line: `TAG OK 100 Hz drops 0 steer +0.00 climb +0.00 [Z] zero tag light 12 lx`. `TAG paused` = press D2.

**Real light.** `EdgeLightEffects` learns the room's normal light from `LUX`. A jump above the threshold (at least 150 lx and 3× normal) is a phone flash; light that stays up longer than 0.5 s is a held torch: torch glare, pulsing buzz and, for the light-averse bat, the same budget/energy drain as standing under a lamp (`realGameLux` sets how hard). Normal light is not re-learned while a torch is held, for up to 30 s. Press **H** in the feeding game: the debug line shows `lux 1.00 (real 1.00)`. The light-tolerant bat ignores it.

## 5. Axis remap (mounting orientation unknown)
Select the Bat → `EdgeTagInput` (added automatically at Play; add it to the Bat to keep settings):
* Tilt right should give `steer +`; if it's climb that moves, set **Steer Source = Pitch**, **Climb Source = Roll**.
* Wrong direction → tick **Invert Steer / Invert Climb**.
* Too twitchy/slow → `Steer Full Scale Deg` (35), `Sensitivity`, `Deadzone` (0.06), `Response Curve`.
* Jitter at rest → lower `Min Cutoff Hz`; lag while moving → raise `Beta`.
Turn feel: `BatFlightController` → Max Bank, Tag/Key Bank Response, Turn Gain, Max Yaw Rate.
Camera: `CameraToggle` → Position/Rotation Sharpness, Turn Look Ahead.

## 6. Test without hardware
`python3 "Final Project/tools/fake_tag.py"` (sine weave) or `--mode keys`, `--drop 0.2`, `--stall 3`, `--legacy`.

Real light only: `python3 Firmware/tools/fake_light.py` (type `f` + Enter for a flash, `t` for a torch on/off) or `--scenario demo`. It sends `LUX` only, so the keyboard still flies the bat; `--ori` adds a level tag stream.

## 7. Troubleshooting
| Symptom | Fix |
|---|---|
| HUD `no packets on UDP 1234` | tag not on hotspot (`wifi=0`), wrong host IP, firewall, another app on 1234 (`lsof -i :1234`) |
| Console `UDP listen failed` | port busy — quit fake_tag/other Unity instance |
| `TAG LOST` flicker | weak Wi-Fi; firmware disables power save already; move closer |
| Bat drifts straight-line turn | re-zero (Z); increase Deadzone |
| Something broke | `BatFlightController → Input Source = LegacyUdpDirect` restores the old raw path |
