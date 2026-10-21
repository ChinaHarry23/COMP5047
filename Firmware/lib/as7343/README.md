# as7343: register-level driver for the AS7343 spectral sensor

Arduino-ESP32 (core 3.x) driver for the ams-OSRAM AS7343 on the Pimoroni
PIM707 breakout. It uses `Wire` only. It was written from the datasheet and
cross-checked against the SparkFun, Pimoroni and Adafruit libraries (see
Sources). No third-party code or tables were copied.

Status: it builds with zero warnings under `-Wall -Wextra` in the `o1_logger` (env archived 16 Oct 2026)
env. **It has not yet run on hardware** (the parts arrive Wed 7 Oct). The
bring-up checks at the end of this file say what to confirm first.

## Files

| File | Contents |
|---|---|
| `src/AS7343.h` / `AS7343.cpp` | the driver (public API below) |
| `src/as7343_channels.h` | channel order and names, `isFilteredChannel()`, `saturationCount()`. It has no Arduino dependency, so host tools can include it (`tools/o1/host_check.cpp` tests it) |
| `library.json` | PlatformIO metadata. Any env that includes `AS7343.h` picks the library up through the LDF |

## Public API

```cpp
#include "AS7343.h"
as7343::AS7343 sensor;

bool  sensor.begin(Wire, PIN_I2C_SDA, PIN_I2C_SCL);  // true if the sensor answered; keeps probing if not
as7343::Event sensor.update(millis());              // call every loop(): kNone / kFrame / kConnected / kLost
const as7343::Frame &f = sensor.frame();            // valid after kFrame
bool  sensor.connected();
void  sensor.restartMeasurement();                  // discard the frame in progress (scene changed)
void  sensor.setIntegration10ms(k);                 // k = 1..42 -> 10..420 ms per SMUX cycle
void  sensor.setGainCode(code);                     // 0..12 = 0.5x..2048x; turns auto-gain off
void  sensor.setAutoGain(bool);
uint8_t sensor.integration10ms(), gainCode(); bool autoGain();
uint8_t sensor.revisionId(), auxId();               // read at connection
const char *sensor.lastFailure();                   // "i2c", "timeout", "settings-lost", "wrong-id", "led-verify"

float as7343::gainMultiplier(code);                 // 0.5, 1, 2 ... 2048
float as7343::basicCounts(frame, as7343::kFXL);     // raw / (gain x integration ms)
bool  as7343::isFilteredChannel(index);             // the 12 filters (not VIS, not CH5)
uint16_t as7343::saturationCount(steps);            // what a saturated channel reads
```

`Frame` holds `raw[18]` in data-register order, plus `gainCode`, `atime`,
`astep`, `integrationUs`, `fullScale`, `saturatedCount`, the saturation flags
(next section but one) and `tMs` (the middle of the measurement window).

## How one frame is measured

1. `startMeasurement()`: ENABLE = PON (SP_EN = 0). Apply any pending gain or
   timing change. Then ENABLE = PON | SP_EN. The datasheet says configuration
   may change only while SP_EN = 0 (DS001046 §10.2.1). Stopping also throws
   away any partial frame, so each frame has exactly one gain and one
   integration time.
2. The device runs three integration cycles (18-channel auto-SMUX). The driver
   does not poll before 3 × t_int has passed. After that it polls STATUS2
   (0x90) for AVALID every 5 ms. AVALID missing for 2 × the frame time
   + 200 ms counts as a failure (`timeout`).
3. `readFrame()`: one 37-byte burst from ASTATUS (0x94) through DATA_17_H
   (0xB8). Reading ASTATUS latches all 36 data bytes, so the 18 values come
   from the same measurement (§10.2.7). Each value arrives low byte first.
   ATIME is then read back. If it does not match, the sensor has reset itself
   (for example a brown-out). The frame is rejected (`settings-lost`) and the
   sensor is reconfigured.
4. Auto-gain, if enabled, picks the gain for the next frame. The next
   `update()` call starts that frame.

## Channel map (18-channel auto-SMUX, CFG20.auto_smux = 3)

Source: DS001046 Fig. 57 (order) and Fig. 7 (typical peak and FWHM).

| Index | Register | Name | Peak (nm) | FWHM (nm) | Cycle |
|---|---|---|---|---|---|
| 0 | DATA_0 0x95 | FZ | 450 | 55 | 1 |
| 1 | DATA_1 0x97 | FY | 555 | 100 | 1 |
| 2 | DATA_2 0x99 | FXL | 600 | 80 | 1 |
| 3 | DATA_3 0x9B | NIR | 855 | 54 | 1 |
| 4 | DATA_4 0x9D | VIS1 (CH4, clear) | broadband | - | 1 |
| 5 | DATA_5 0x9F | CH5_1 (see below) | broadband | - | 1 |
| 6 | DATA_6 0xA1 | F2 | 425 | 22 | 2 |
| 7 | DATA_7 0xA3 | F3 | 475 | 30 | 2 |
| 8 | DATA_8 0xA5 | F4 | 515 | 40 | 2 |
| 9 | DATA_9 0xA7 | F6 | 640 | 50 | 2 |
| 10 | DATA_10 0xA9 | VIS2 | broadband | - | 2 |
| 11 | DATA_11 0xAB | CH5_2 | broadband | - | 2 |
| 12 | DATA_12 0xAD | F1 | 405 | 30 | 3 |
| 13 | DATA_13 0xAF | F7 | 690 | 55 | 3 |
| 14 | DATA_14 0xB1 | F8 | 745 | 60 | 3 |
| 15 | DATA_15 0xB3 | F5 | 550 | 35 | 3 |
| 16 | DATA_16 0xB5 | VIS3 | broadband | - | 3 |
| 17 | DATA_17 0xB7 | CH5_3 | broadband | - | 3 |

That makes 12 filtered channels (indices 0-3, 6-9, 12-15) plus two broadband
ADCs, CH4 and CH5, which are read again in each of the three cycles, which is
why they appear three times. With flicker detection off (ENABLE.FDEN = 0),
ADC5 runs with the same gain and timing as the others (§10.2.2).

**What CH5 reads is not settled.** Fig. 57 lists "2xVIS, FD" for the last two
ADCs, and adds that by default the "Top Left" and "Both Right" VIS photodiodes
are read out. Pimoroni (`vis_tl`, `vis_br`) and Adafruit (`VIS_TL`, `VIS_BR`)
read CH5 as a second clear photodiode; SparkFun (`CH_FD_1`) reads it as the
flicker photodiode. The driver and the O1 log therefore call it `CH5` until
bring-up check 3 has been done. Either way it is the brightest value: in
Adafruit's hardware tests it reads 2-26x the VIS value and sits at full scale
(17999) while every filter is far below it.

For the 590 nm amber lamp, FXL (600/80) is the nearest channel. FY (555/100)
also sees it strongly. F5 (550/35) and F6 (640/50) catch it only on the edges
of their passbands.

## Integration time (multiples of 10 ms)

t_int = (ATIME + 1)(ASTEP + 1) × 2.78 µs (Eq. 1). Full scale = min(65535,
(ATIME + 1)(ASTEP + 1)) (Eq. 2). A saturated channel reads one count below
that full scale (17999 at full scale 18000 in Adafruit's hardware tests,
which run at ATIME 29 / ASTEP 599), or 65535 once Eq. 2 is capped:
`saturationCount()`. The driver fixes ASTEP = 599, so one ATIME step is
1.668 ms, and sets ATIME = 6k − 1:

| k (`setIntegration10ms`) | ATIME | t_int per cycle | Full scale | Saturated reads | Frame (3 cycles), nominal |
|---|---|---|---|---|---|
| 1 | 5 | 10.008 ms | 3600 | 3599 | 30 ms + overhead |
| 5 (O1 default) | 29 | 50.04 ms | 18000 | 17999 | 150 ms + overhead |
| 10 | 59 | 100.08 ms | 36000 | 35999 | 300 ms + overhead |
| 42 (max) | 251 | 420.3 ms | 65535 | 65535 | 1.26 s + overhead |

The datasheet's own 50 ms example uses ASTEP = 599 and ATIME = 29 (Fig. 10).
The 0.08% excess over k × 10 ms leaves almost none of the 100 Hz mains ripple
un-averaged, and it is smaller than the internal oscillator's own tolerance.
So the integration is a multiple of 10 ms only nominally. "Overhead" means the
autozero that runs at each start (typically 15 ms, §10.2.3, AZ_CONFIG left at
its default of "before first cycle") plus SMUX switching. The real frame rate
has not been measured yet (bring-up check 2).

## Gain, auto-gain and saturation

- CFG1.AGAIN codes 0..12 select 0.5x, 1x, 2x ... 2048x. The reset value is 9 (256x).
- The AS7343's built-in AGC covers only the flicker channel (AGC_GAIN_MAX,
  Fig. 34), so this driver does spectral auto-gain in software, after each
  frame, on the **12 filtered channels only**. Letting the broadband CH4/CH5
  set the gain would hold the amber channels about 10x below full scale
  (CH5 pins at full scale while FXL reads 1456 in Adafruit's test 02):
  - **Step down** one code if the brightest filter is above 80% of full
    scale, two codes (4x) if the analog flag is attributed to the filters
    (below), because analog saturation hides how far over range the light is.
  - **Step up** one code if the brightest filter is below 20% of full scale.
    One step is at most 2.10x (Fig. 10), so it lands below 42% and the gain
    does not hunt.
- Saturation flags in `Frame`:

  | Field | Meaning |
  |---|---|
  | `digitalSaturation` | a filtered channel reads `saturatedCount` |
  | `analogSaturation` | `chipAnalogFlag` while a filtered channel is above 80% of full scale |
  | `chipAnalogFlag` | STATUS2.ASAT_ANALOG, as read |
  | `chipDigitalFlag` | STATUS2.ASAT_DIGITAL or ASTATUS.ASAT_STATUS, as read |

  `analogSaturation || digitalSaturation` means "do not trust this frame's
  filtered channels"; the exhibit's adapter uses exactly that. The chip's own
  flags are global: they name no channel, and with the gain set by the
  filters the broadband CH4/CH5 trip them routinely. Adafruit's driver also
  notes that the analog flag "can report saturation even when channel
  readings appear normal". So the analog flag counts against the filters only
  when one of them is near full scale. This assumes an over-range analog
  stage drives its channel's count high; bring-up check 3 tests it. The O1
  logger logs the chip flags in their own columns and leaves the per-channel
  decision to the analysis.
- `basicCounts()` divides by the *nominal* gain. Fig. 10's typical gain
  ratios (relative to 64x) put adjacent steps at 1.94-2.10x rather than 2x,
  and at 2048x the min-max range is 28.2-41.3 against a nominal 32 (−12% to
  +29%). Until E0 calibration, frames are compared only at one gain: O1 locks
  the gain, and its analysis never pairs frames taken at different gains.

## Robustness

- **Never blocks indefinitely.** Every `Wire` call is bounded by
  `Wire.setTimeOut(20)`. `update()` does at most a few short transactions per
  call.
- **No abort on a missing sensor.** `begin()` returns false and `update()`
  keeps probing every 500 ms. A missing sensor is a normal state.
- **Hot unplug.** Three consecutive failures (I2C error, AVALID timeout, or
  settings lost) give `kLost`. A single glitch on the 500 mm cable is
  retried, after a full reconfigure.
- **Replug / bus clear.** Before every probe, the driver releases the I2C
  peripheral and clocks SCL up to 9 times until SDA is released. It then
  sends a STOP and restarts `Wire` (NXP UM10204 Rev. 7 §3.1.16 "Bus clear").
  This frees a sensor that was cut off mid-byte. It takes about 0.1 ms.
- **Full reconfigure on every (re)connection.** The part ID is checked
  (ID register 0x5A = 0x81, bank 1). Then PON, LED off, auto-SMUX, timing and
  gain are written. Reserved bits are preserved by read-modify-write where a
  register mixes fields (CFG1, CFG20).
- **Illumination LEDs.** The PIM707's two white LEDs are driven from the
  AS7343 LDR pin. The LED register (0xCD) is written to 0x00 (LED_ACT = 0,
  minimum 4 mA drive) and read back on every (re)connection. If LED_ACT does
  not read back 0, the connection is refused (`led-verify`). The driver never
  sets LED_ACT. A sensor that resets comes back with LED_ACT = 0 (its reset
  value).

## Bring-up checks (do these first on Wed 7)

1. ID read succeeds (`# AS7343 found (rev …)` in the O1 logger banner) and the
   two white LEDs on the PIM707 stay dark.
2. Frame rate: rows should arrive about 5 times a second, with the last
   digits changing from row to row. If frames arrive every ~150 ms with
   identical values, AVALID is not clearing when SP_EN is toggled. That
   behaviour is assumed from the datasheet's state description and is not yet
   seen on hardware. Report it, and the driver will need an explicit wait.
3. Covering the sensor lowers every channel, and a phone torch raises them.
   Auto-gain steps down under the torch (press `h` for the current gain).
   Then, once the gain is locked (PROTOCOL step 7), read a few rows with the
   lamp off, a few at lamp max and a few with the torch on the bat, and write
   down:
   - `asat_ana` and `asat_dig`. If either is 1, look for a channel at the
     saturation count (17999 at 50 ms): CH5 or VIS at full scale explains it.
     If `asat_ana` is 1 while no channel at all, CH5 included, is near full
     scale, the flag misfires or the assumption above does not hold: note the
     row and report it.
   - CH5_1 / FXL and CH5_1 / VIS1 at lamp max. They show how far below CH5
     the amber channel sits, and they are the data for settling what CH5 is.
4. Unplug the Qwiic cable for 2 s and replug it: `# AS7343 lost (…)`, then
   `# AS7343 connected` within about 1 s (TP1 asks for under 2 s).

Cross-check notes:
- SparkFun's library enables 18-channel auto-SMUX and SP_EN without SMUXEN,
  as this driver does. Pimoroni's Python library also sets ENABLE.SMUXEN and
  reads the FIFO instead. The datasheet describes auto-SMUX as automatic, so
  this driver does not set SMUXEN.
- SparkFun reads the data from DATA_0 (0x95). This driver starts at ASTATUS
  (0x94) because the datasheet says reading ASTATUS latches the data set.

## Sources

- ams-OSRAM, *AS7343 14-Channel Multi-Spectral Sensor*, datasheet DS001046,
  **v6-00, 2023-06-07**.
  https://look.ams-osram.com/m/5f2d27fff9a874d2/original/AS7343-14-Channel-Multi-Spectral-Sensor.pdf
  Used for: §8 (power-up and states), §9 (I2C, 16-bit latching), §10.1
  register map, §10.2.1 (ENABLE, LED), §10.2.2 (ATIME/ASTEP, Eq. 1–2),
  §10.2.3 (CFG1 AGAIN, AZ_CONFIG, AGC_GAIN_MAX), §10.2.4 (ID), §10.2.6
  (STATUS, STATUS2), §10.2.7 (ASTATUS latch, data), §10.2.8 (CFG0
  REG_BANK, CFG20 auto_smux), Fig. 7 (channel peaks), Fig. 10 (gain ratios,
  timing example).
- SparkFun Electronics, *SparkFun_AS7343_Arduino_Library*,
  `src/sfTk/sfDevAS7343.h/.cpp` and Example 01 (MIT licence),
  https://github.com/sparkfun/SparkFun_AS7343_Arduino_Library. Read for
  cross-checking only: register addresses, the bit layouts and the 18-channel
  order agree with the datasheet. It names data index 5 `CH_FD_1`. No code or
  tables were copied.
- Adafruit Industries, *Adafruit_AS7343*,
  https://github.com/adafruit/Adafruit_AS7343 (read 5 Oct 2026):
  `Adafruit_AS7343.h` (index 5 = `AS7343_CHANNEL_VIS_BR_0`),
  `Adafruit_AS7343.cpp` (`begin()` sets ATIME 29 / ASTEP 599; the note on
  `isAnalogSaturated()`), and the recorded outputs in `hw_tests/`
  (02_spectral_read_test, 03_gain_test, 05_smux_mode_test): saturated
  readings of 17999 at full scale 18000, CH5 at 2-26x VIS, and F4 scaling
  cleanly from 1x to 2048x while CH5 is saturated. Read for cross-checking
  only; no code was copied.
- Pimoroni, *AS7343 14-Channel Multi-Spectral Sensor Breakout* (PIM707)
  product page: I2C address 0x39, two on-board illumination LEDs, 3.3 V or 5 V
  supply, 19 × 20 × 5 mm. https://shop.pimoroni.com/products/as7343-breakout
- Pimoroni, *as7343-python*, `as7343/__init__.py`,
  https://github.com/pimoroni/as7343-python. Read for cross-checking the
  initialisation sequence and the channel names (`vis_tl`, `vis_br` in every
  cycle) only.
- NXP, *UM10204 I2C-bus specification and user manual*, Rev. 7.0, §3.1.16
  "Bus clear": nine clock pulses when SDA is stuck LOW.
