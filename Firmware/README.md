# EDGE Revision 3 firmware

Firmware for the FireBeetle 2 ESP32-E (DFR0654) and a host simulator for its policy.
This is a technical README: how to build, run and test it, how it is put together,
and where the numbers come from. Plan references (§) are to `REVISION_3_PLAN.md`
(now `../../archive/rev3-exhibit/REVISION_3_PLAN.md`).

> **16 Oct 2026.** The game's bat controller is env `tag` (`src/tag/`), now the default env; it is
> documented in [`../../docs/CIRCUIT_GUIDE_v1.md`](../../docs/CIRCUIT_GUIDE_v1.md), not here. The
> `o1_logger` env and `src/o1/` were archived to `../../archive/rev3-exhibit/firmware/src/o1/`.
> `src/kit/` stays because the host simulator compiles `src/kit/kit_light.cpp` (`sim/Makefile`).

- **`lib/core/`**: the policy, in plain C11 with no Arduino headers. It compiles unchanged on the board and on a laptop.
- **`sim/`**: unit tests and a scenario simulator. They run the real `lib/core` and assert invariants I1-I9 (§7.4) on every control step.
- **`src/kit/`** (env `kit`): FW0, the check-in loop on unit-kit parts.
- **`src/exhibit/`** (env `exhibit`): the full exhibit app. The bat's spectral input comes from the AS7343 driver in `lib/as7343` through a small adapter (§7).
- **`include/`**: pins and code shared by the targets. PlatformIO compiles only one `src/` subfolder per target, so shared code lives here.

`lib/as7343/` (the AS7343 driver), `src/o1/` (env `o1_logger`, the O1 optics logger; archived 16 Oct) and `tools/` belong to other work packages and have their own READMEs. This file describes only how the exhibit uses the driver (§7).

---

## 1. Quick start

```sh
# host tests: 5 unit suites + 29 scenarios; exits non-zero on any failure
cd sim && make test

# firmware (from rev3/firmware)
PLATFORMIO_SETTING_ENABLE_TELEMETRY=no ~/.platformio/penv/bin/pio run -e kit
PLATFORMIO_SETTING_ENABLE_TELEMETRY=no ~/.platformio/penv/bin/pio run -e exhibit

# flash and watch (board on USB; VCC jumper OFF while flashing, §6)
~/.platformio/penv/bin/pio run -e kit -t upload && ~/.platformio/penv/bin/pio device monitor
```

Build results on 5 Oct 2026, after the review fixes (pioarduino 55.03.311, Arduino-ESP32 3.3.11, ESP-IDF 5.5.5). Each is a clean build of the target with zero warnings:

| Target | RAM | Flash (of 1,310,720 B app partition) |
|---|---|---|
| `kit` | 24,188 B (7.4%) | 351,732 B (26.8%) |
| `exhibit`, no `include/secrets.h` (no Wi-Fi code linked) | 30,948 B (9.4%) | 431,908 B (33.0%) |
| `exhibit` with a `secrets.h` (Wi-Fi + TLS linked; built with a dummy file on an extra include path) | 59,816 B (18.3%) | 1,199,862 B (91.5%) |
| `o1_logger` (archived 16 Oct) | 24,868 B (7.6%) | 354,540 B (27.0%) |

The `exhibit` + `secrets.h` row is what the exhibit will really be. It fits, with about 110 KB to spare (see §9).

---

## 2. Layout

```
lib/core/            pure C11 policy (host + board)
  edge_config.h      every declared/tunable number, one-line basis each
  spectral.h/.c      edge_spectral_frame_t: the contract with any spectral driver
  calib.h/.c         two endmembers (amber lamp, broadband) + photopic/V' factors
  edge_light.h       edge_light_t: what the light layer tells the policy
  sep2.h/.c          two-endmember separation, steady-frame gate, baseline, F6, scene change
  dose.h/.c          photopic and V'-proxy lux, FMM integration, nightly tally, past zero
  forecast.h/.c      Open-Meteo parser, tonight's C, tighten-only resolution, B(C), cache rule
  http_date.h/.c     HTTP Date header -> UTC epoch (dates a fetch without SNTP)
  startup_seq.h/.c   startup sequence: sweep, fetch with 5 s cap, sky factor, hand-over
  budget_fsm.h/.c    the six states, the single stepper, ramps, floor invariant
  presence_filter.h/.c  learned threshold, hysteresis, 100 ms debounce
  dial.h/.c          needle angles: budget, past-zero, sky factor, sweep, FAULT
sim/
  Makefile           `make test`
  edge_sim.c         scenario runner (app wiring + plant model + invariant checks)
  scenarios/*.scn    29 scenarios
  test_*.c           unit tests (sep2, dose, forecast + HTTP date, presence/dial/startup)
  test_kit.cpp       kit light adapter + needle/touch headers against the real core
  fake_arduino/      the few Arduino calls test_kit needs (host only)
  fixtures/          captured Open-Meteo response + three synthetic variants
  plant_shapes.h     synthetic room-light spectra (simulator only)
  tools/gen_endmembers.py  regenerates the placeholder spectra
include/
  pins.h             pin map (checked against the DFR0654 wiki)
  lamp_pwm.h         20 kHz / 11-bit lamp output, gamma already applied by the FSM
  needle_servo.h     FS90 at 50 Hz on its own timer, pulses stopped after each move
  touch_presence.h   capacitive touch presence (kit foil; exhibit fallback)
  debounced_button.h active-low button with internal pull-up
  secrets.h.example  Wi-Fi template; copy to secrets.h (git-ignored)
src/kit/             FW0: main.cpp, kit_light (phototransistor), kit_presence
src/exhibit/         main.cpp, presence, operator_io, net, logbook, calib_store,
                     bench_cal, spectral_source.h + spectral_as7343.cpp, recorded_nights.h
lib/as7343/          AS7343 register-level driver (own README; used by exhibit and the archived o1_logger)
```

---

## 3. Architecture

```
 hand on mark ──> presence_filter ──────────────┐
                                                 │ present
 bat sensor ──> SpectralSource ──> sep2 ─────────┤ edge_light_t ──> edge_fsm_step()  every 50 ms
 (kit: phototransistor ──> KitLight) ────────────┘                    │   │
                                                                      │   ├─> lamp level ──> LampPwm
 net / NVS cache / recorded nights ──> startup_seq ──> budget B ──────┘   └─> dose tally ──> dial ──> FS90
```

**One stepper.** `edge_fsm_step()` is the only function that moves the lamp or spends budget, and each app calls it from exactly one place, every `EDGE_STEP_MS`. This fixes the progress check's double-stepping bug. Spectral frames (about 5 Hz) update the light estimate between steps. The step charges the light emitted over the step that just finished.

**The light layer boundary.** The FSM never sees spectra. It sees `edge_light_t`: the lamp component, the signed broadband change, the F6 flag, and validity. In the exhibit, `sep2` produces it from AS7343 frames. In the kit, `KitLight` produces it from one phototransistor channel, treating the whole rise as lamp light. Because the struct is the same, the kit runs the exhibit's policy unchanged.

**States** (§7.3), as implemented in `budget_fsm.c`:

| State | Lamp target | Leaves when |
|---|---|---|
| STARTUP | 0 | budget handed over **and** room learned → DARK_IDLE / LIT / YIELD |
| DARK_IDLE | 0 | presence → LIT; F6 event → YIELD |
| LIT | floor + (max − floor) × clamp(remaining / B, 0, 1), in perceptual level | hand leaves → FADING; F6 → YIELD |
| YIELD | floor if present, else 0 | event ends, re-baseline, or 30 s scene change → LIT / FADING / DARK_IDLE |
| FADING | 0 | presence → LIT; level reaches 0 → DARK_IDLE |
| FAULT | floor if present, else 0 | sensor valid again for 1 s → previous context (STARTUP if the night was never sized) |

These rules hold in every state:

- **Ramps, not steps.** Up at 1.5 s full scale. Down at 5 s full scale. YIELD and FAULT dim at 0.6 s full scale.
- **Floor invariant.** While someone is present and the lamp is at or above the floor, the target is never allowed below the floor. That covers a new night mid-visit, where STARTUP asks for 0.
- **FAULT** is entered when no valid reading has arrived for 1 s. It never latches and never reboots. An invalid reading keeps the light layer's last event flag, so a YIELD in progress holds until FAULT takes over instead of ramping up for the last second (scenario 27).
- **Night** is the startup baseline plus the operator's new-night button. The wall clock is never used.

**Actions, not states:**

- `edge_fsm_new_night()` re-runs STARTUP and resets tonight's tally.
- A re-baseline (button, or the 30 s scene-change rule) happens in the light layer only: `edge_sep2_rebaseline()`. It clears the event and changes `room_epoch` (`edge_light.h`); the FSM sees the event end with a new epoch and does not charge it. The budget is not touched (I8).
- A forecast that arrives after the 5 s cap can only tighten: before the hand-over through the startup sequence, after it through `edge_fsm_tighten_budget()`.

**F6 events are charged when they end.** Plan F6 charges an event's night-vision dose, and its 30 s rule says an "event" that lasts longer was the room changing. So while an event runs its dose waits in `event_pending`. When the event ends it is committed to the tally; when it ends because the room was re-learned (scene change, re-baseline button), it is dropped. What remains never rises either way (I3). Two visible consequences: room lights switched on and left on cost nothing beyond the lamp's own light (TP15, scenarios 17 and 28), and during the torch beat the needle and the pull-back move when the torch is taken away, not while it is held. This reads TP15's "budget unchanged" as "remaining unchanged"; it needs a team ruling (§9).

**Fades are rate-based, and the level is perceptual.** These are two deviations from the plan text that the team must rule on and defend (§9):

- **Fade time depends on where the fade starts.** The ramp moves at fixed full-scale rates, so a fade from level L takes L × 1.5 s up and L × 5 s down. Only fades from maximum take the plan's 1.5 s and 5 s. A fade out from the floor takes 0.45 × 5 = 2.25 s. TP3 must therefore be timed from maximum: fade up from dark at the start of a night, and fade out after a hand-off at maximum. Scenario 02 bounds both from above and below, on a clear night that keeps the lamp near maximum. The fixed rate keeps the perceived speed constant.
- **F4's interpolation is perceptual, not lux-linear.** The FSM interpolates the perceptual level, `level = floor + (max − floor) × remaining / B`, and the lamp emits `level^2.2`. With the placeholder floor of 0.45, half the budget gives 0.725^2.2 = 0.49 of maximum light. F4 as written in tile lux (floor and max in lux) would give 0.17 + (1 − 0.17) × 0.5 = 0.59. E3's analysis must use the implemented law, or the team switches to lux-linear interpolation.

---

## 4. Core modules: what each decides, and why

### sep2: two-endmember separation (§7.2, F3, F6)

Each frame `y` (12 AS7343 channels in basic counts) is explained as follows.

```
y = baseline + a · shape_lamp + w · shape_broad + residual
```

- **The baseline is a whole spectrum.** It is the room's own light, learned with the lamp's estimated part removed. Room light of any shape, including fluorescent lines, is absorbed exactly while it stays constant. Unit test `test_fluorescent_room_absorbed` checks this. A scalar broadband baseline would instead leak a fluorescent room into the lamp estimate all the time.
- **The solve.** `a` is solved by 2×2 least squares with `a ≥ 0`. `w` is the broadband change and is signed, so room lights going off are a negative `w`, not a negative lamp. If the two shapes are too alike (1 − cos² < `EDGE_COLLINEAR_LIMIT`), the solve is refused.
- **Steady-frame gate.** A frame is unmixed only if the commanded light changed less than 5% across its own integration window. `sep2` keeps 3.2 s of commanded light, and the frame carries `t_start_ms` and `t_end_ms`. On other frames (fades, saturation), `a = lamp_gain × mean commanded light`, where `lamp_gain` is the transfer measured on recent steady frames.
- **Lamp off means `a = 0` by construction.** The BC337 stage emits nothing at duty 0, so any amber-like change is the room's. This is what keeps TP11 at zero dose.
- **The baseline adapts slowly** (τ = 30 s, placeholder), and only while the lamp is off and nothing is happening. It is frozen while lit and during events.
- **F6 event detector**, on `w` converted to photopic lux. An event needs a *fast* rise above HIGH (from below LOW within 2 s) that holds for 0.4 s. It ends after 1 s below LOW. A slow drift is never an event.
- **Scene change.** Any offset above HIGH lasting 30 s re-learns the room and ends the event: an event that never ends, a slow drift, or lights switched off for good. `sep2` has no access to the budget, so a re-baseline cannot change it (I8).
- **Sensor return.** A sensor that comes back after a gap may be the spare bat, so the room is re-learned.
- **The contract with the FSM** (`edge_light.h`). An invalid frame changes only `valid`; the event flag and `lamp_gain` keep their last values. Every re-learn (button, scene change, sensor return) bumps `room_epoch`.

**Known limit**, which the unit test pins to the closed-form projection and prints: room LEDs and torches share the broadband endmember. A warm-white room that *changes* while the lamp is lit leaks into the lamp estimate. With the placeholder shapes, the leak is about 0.57 lamp-lux per room-lux. A constant room does not leak, a broadband-shaped change does not leak, and nothing leaks while the lamp is off. E0 should capture the real room's spectrum.

### dose (§4)

**Factors.** Photopic lux equals amplitude × `phot_per_amp`. V′-proxy lux equals photopic × the endmember's S/P.

**Unit.** 1 FMM = 0.2 lux × S/P 1.91 = 0.382 V′-proxy lux for one night minute. One exhibit second is one night minute.

**What is charged:**

- The lamp component is charged always.
- Without a valid reading, the lamp is charged as `lamp_gain × commanded light`, taken from the light layer. `lamp_gain` is the last measured transfer, or the expected one if the sensor never answered, so a bat missing from boot does not make the floor free (scenario 26). Losing the sensor never makes light free.
- Positive broadband excess is charged only while an F6 event runs and readings are valid. The 0.4 s confirmation hold is not charged. The FSM holds the event's charge aside until the event ends (§3).

**Tally.** The tally keeps spent, lamp, event and photopic totals. Spending continues past zero, and the needle's past-zero sector shows it.

### forecast (§9, F5)

**Parser.** The parser is purpose-built: no JSON library and no heap. It first checks that the whole body is one complete JSON value, which is what makes every truncated body fail. Unit test `test_every_truncation_rejected` tries every prefix of the real response. The parser is tolerant of field order, unknown fields, escapes and `null` samples. `{"error":true}` is reported as `api-error`.

**Tonight's C** is the mean hourly cloud cover from the sample at the top of the sunset hour up to local midnight. It uses only the response's own `daily.sunset` and `utc_offset_seconds` (`timeformat=unixtime`). For the captured 5 Oct response, this gives the plan's numbers: C = 0.958 (5 Oct) and 0.008 (6 Oct).

**Budget.** B = B_max / (1 + (m − 1)·C), with m = 3.

**Tighten-only rules:**

- A cache older than 24 h gives C = 1.
- A cache of unknown age (saved before the clock was known, or no clock now) gives C = 1.
- No data at all gives C = 1.
- An evening with no samples gives C = 1.
- Recorded nights are chosen deliberately with the selector, so age does not apply to them.

**Which record is kept** (`edge_sky_cache_replaces`). An undated record never replaces a dated one, because the dated one becomes usable whenever the clock is known and the undated one never does. Between dated records the newer wins. NVS (`net.cpp`) and RAM (`main.cpp`) follow the same rule.

**Dating a fetch** (`http_date.c`). Every answer from an origin server with a clock carries a Date header (RFC 9110 §6.6.1), so a fetch dates itself from the response's Date header in IMF-fixdate form. SNTP is only the fallback. See §7 for why this matters on a board with no battery-backed clock.

**Temperature** (`temperature_2m`) is parsed and logged as the evening mean only. It is not part of the control law (§4).

### startup_seq (§1.3, F5)

1. **SWEEP:** a 2 s needle self-test, at boot only. A live fetch is already running in the background.
2. **WAIT_FETCH:** waits until the fetch answers, or until 5 s have passed since the start. Then it resolves the sky as live, cached, recorded or protective.
3. **SHOW_SKY:** shows C on the inner arc for 3 s. A live answer that arrives now, after the cap, may only tighten the sky that is shown and handed over (scenario 25). Before this fix such an answer was dropped and the clearer cache was handed over.
4. **DONE:** hands B to the FSM once. A later answer goes to `edge_fsm_tighten_budget()`.

The selector is read only when the sequence begins, so turning it mid-night changes nothing until the next new night (scenario 19).

### presence_filter (F1)

At boot the filter learns the no-hand mean and SD. The press threshold is mean + max(6·SD, min_delta). The release threshold is 40% of that margin lower. A change must hold for 100 ms before it is reported.

A boot reading above `EDGE_FORCE_LEARN_CEILING` means a hand was already on the mark. The filter then falls back to defaults rather than learning the hand (scenario 22).

### dial

Angles are in servo degrees. The geometry is a **placeholder** until the dial face exists.

- **Outer arc:** the budget on an absolute FMM scale, with full scale = B_max. Overcast nights therefore start visibly lower.
- **Past-zero sector:** beyond zero, showing up to 0.5 B_max of overspend.
- **Inner arc:** C.
- **FAULT tick.**
- **During startup:** the sweep, then a neutral park while fetching.

`edge_dial_target_deg()` is the one function both apps and the simulator use.

---

## 5. Declared numbers and placeholders

All live in `lib/core/edge_config.h`, each with a one-line basis comment. The values below are **placeholders** until the named test replaces them. Change them there, rebuild, run `make test`, and log the change.

| Number | Value now | Replaced by |
|---|---|---|
| `EDGE_LEVEL_FLOOR` / `EDGE_LEVEL_MAX` | 0.45 / 1.0 perceptual (5.8:1 in light at γ 2.2) | tile-lux tuning, ≥ 5:1 (W10) |
| `EDGE_LAMP_PHOT_LUX_AT_MAX` | 4.0 lux at the bat | O1 / E2 |
| `EDGE_B_MAX_FMM` | derived: 25 s at max ≈ 60.2 FMM | follows the line above; freeze in W10 |
| `EDGE_SP_AMBER` / `EDGE_SP_BROAD` | 0.23 / 2.09 (literature values via §4) | E4 measured spectra |
| endmember shapes (`calib.c`) | analytic model (`sim/tools/gen_endmembers.py`) | E0 / E4 captures |
| lamp lux per amplitude | 1.0 until `cal` | `cal [lux]` at the bench, then E0 |
| `EDGE_F6_HIGH_LUX` / `LOW` | 2.0 / 1.0 lux | O1 (≥ 5× room-drift SD) |
| `EDGE_BASELINE_TAU_S` | 30 s | O1 drift data |
| `EDGE_FORCE_MIN_DELTA` | 400 ADC counts | TP2 with real hands |
| `EDGE_TOUCH_MIN_DELTA` | 8 counts | fallback electrode test |
| `EDGE_LAMP_MIN_DUTY` | 2% | measure the BC337 stage onset on arrival |
| `EDGE_KIT_PHOTO_DELTA_AT_MAX` | 1000 counts | kit `cal` |
| `EDGE_NEEDLE_QUIET_MS` | 40 ms of touch samples skipped after each needle move | bench test of the touch fallback with the servo moving (servo coupling is unverified; 0 if none is seen) |
| dial angles | 12–110° of the FS90's ~120° | the dial face |
| `EDGE_SITE_LAT/LON` | the plan's forecast-check coordinates | the chosen field site |

Firmware choices with a stated basis (not from the plan):

- `EDGE_NEEDLE_MIN_INTERVAL_MS` = 200 ms: needle moves at most 5 times a second, so touch is sampled for at least 150 ms between moves (a `static_assert` in `needle_servo.h` checks 200 − 40 ≥ 150).
- `EDGE_KIT_SATURATED_COUNTS` = 4000: the 12-bit ADC tops out at 4095.
- `EDGE_KIT_MIN_LED_COUNTS` = 20 counts per unit of light: below this the phototransistor cannot see the kit LED (`cal` refuses below it too).
- `EDGE_KIT_CHECK_LIGHT` = 0.15: the LED test runs only at or above this light, which must stay below the floor's light (0.45^2.2 = 0.17).

Declared (not measured) by the plan:

- full moon = 0.2 lux, S/P 1.91;
- 60× time compression;
- m = 3;
- fades 1.5 s and 5 s;
- 20 kHz / 11-bit PWM;
- F6 hold 0.4 s and end 1 s;
- 30 s scene change;
- 24 h staleness;
- 5 s fetch cap;
- 6 h refresh.

`EDGE_DIM_TO_FLOOR_MS` = 600 ms is sized for TP6. The budget is one 0.2 s frame of detection latency, plus the 0.4 s hold, plus (max − floor) × 0.6 s, which must stay at or under 1.0 s. If the floor/max gap grows, recheck it (scenario 05 asserts it). That budget assumes 0.2 s frames; at the AS7343's real frame period it has no margin (§7, "Things to check on Wed 7").

---

## 6. Kit (FW0): check-in loop on unit-kit parts

| Part | Pin | Wiring |
|---|---|---|
| Kit pushbutton (presence) | GPIO23 | button to GND (internal pull-up) |
| or kitchen-foil touch pad | GPIO4 (T0) | foil on a wire; `touch` command selects it |
| Kit LED (lamp) | GPIO25 | 220 Ω → LED → GND |
| Phototransistor (the bat's stand-in sensor) | GPIO39 (A1) | 3V3 → collector; emitter → GPIO39 and 10 kΩ → GND |
| FS90 needle | GPIO26 | signal; V+ from the 5 V rail, never the 3V3 regulator; common GND |
| optional buttons | GPIO13 re-baseline, GPIO14 new night | to GND |

**Behaviour.**

- At boot: a needle sweep, then the protective sky (C = 1). FW0 has no network, so B = B_max/3 ≈ 20 FMM, and the pull-back comes within about 10 s of holding.
- Hand on: 1.5 s fade up. The needle falls, and the light pulls back to the floor and never below.
- Hand off: 5 s full-scale fade. The needle holds.
- Nobody there: nothing is spent. With the LED off the lamp component is zero by rule, as in the exhibit (TP11), so ADC noise is never charged. `test_kit` checks one minute of idle with noisy readings.
- The phototransistor is sampled once a millisecond and averaged over each 50 ms control step, which also averages out 100 Hz mains flicker.
- **FAULT, as in the exhibit.** A reading is invalid when the ADC is at its top (≥ 4000 counts), or when the LED is lit and adds less than 20 counts per unit of light (phototransistor unplugged or looking away). After 1 s without a valid reading the core enters FAULT: floor if someone is present, the floor still charged from `lamp_gain`, the needle on the FAULT tick. Visibility can be tested only while the LED is lit, so FAULT from a missing LED clears on the next visit, once the phototransistor sees the LED again.

**Serial** (115200) prints a status line every second.

| Command | What it does |
|---|---|
| `cal` | Measures how many ADC counts the LED adds at full light, with the phototransistor where it will sit, and saves it in NVS. Run it once after wiring. |
| `night` | New night. |
| `rebase` | Re-learns the dark level the next time the LED is off. |
| `touch` / `button` | Picks the presence input; saved in NVS. |

**Limits.** One channel cannot separate the lamp from other light. Room-light changes while lit are charged as lamp, and there is no F6 in FW0. A room that darkens by more than the LED's contribution while the LED is lit looks the same as an unplugged phototransistor, so it also gives FAULT until the next visit. The kit's dose is the lamp's light normalised by `cal` to the declared lux-at-max. It shows the control loop, not a measurement.

**Idle check before the demo.** Leave the kit for 60 s with nobody on the button or foil. The status line's `spent` must stay at 0.0 and the needle must not move.

---

## 7. Exhibit

**Pins** (`include/pins.h`, checked against the DFR0654 wiki):

| Pin | Use |
|---|---|
| 21 / 22 | I²C to the AS7343 |
| 25 | Lamp (BC337 base via 220 Ω) |
| 26 | FS90 |
| 36 | Force sensor (3V3 → RP-S40-ST → GPIO36, 10 kΩ → GND) |
| 4 | Touch fallback |
| 13 / 14 | Re-baseline / new night |
| 34 / 35 | Selector (external 10 kΩ pull-ups; A low = REC-CLEAR, B low = REC-OVERCAST, both high = LIVE) |
| 18 / 19 | Bicolour operator LED, green / red, via 330 Ω |
| 2 / 5 | Onboard LED and RGB, held off |

**Boot options:**

- Hold **new night** to use the touch fallback instead of the force sensor. Touch samples are skipped for 40 ms after each needle move, and needle moves are at least 200 ms apart, so a hand leaving is seen within 150 ms even while the needle drains (`test_kit` sweeps the release across the needle's moves: worst 140 ms on the host model). The force sensor is never gated.
- Hold **re-baseline** to switch to the other bat unit. The operator LED blinks the unit number, and each unit keeps its own `cal` in NVS (§7.6).
- Keep hands off the mark for the first half second: presence learns then.

**Operator LED:**

| LED | Meaning |
|---|---|
| Green | Live forecast |
| Amber | Cached forecast |
| Amber blinking | Protective budget (stale, unknown age, or no data) |
| Green blinking | Recorded night |
| Red blinking | FAULT |
| Off | Startup |

**Wi-Fi.** Copy `include/secrets.h.example` to `include/secrets.h` (git-ignored) and use a 2.4 GHz WPA2 hotspot. Without the file the exhibit still builds and runs; it reports that it has no live fetch.

**How a fetch runs.** Each fetch does connect, SNTP, HTTPS GET, parse, NVS save and disconnect in its own task on core 0. The server certificate is checked against the ESP-IDF CA bundle; the precompiled mbedTLS has `CONFIG_MBEDTLS_HAVE_TIME_DATE` off, so certificate dates are not checked and TLS works before the clock is set. The control loop never waits; the startup sequence stops waiting at 5 s. A later answer can only tighten B. In LIVE mode, the forecast refreshes every 6 h.

**The clock, and what TP7 can show.** The FireBeetle has no battery-backed clock, so after every power-up the time is unknown, and a cache of unknown age gives C = 1.

- A fetch that gets any answer, error answers included, sets the clock from the response's Date header (`settimeofday`). Open-Meteo sends one (`Date: Mon, 05 Oct 2026 09:44:30 GMT`, observed with `curl -I` on 5 Oct 2026).
- SNTP is the fallback. lwIP delays its first request by a random 0–5 s (`CONFIG_LWIP_SNTP_STARTUP_DELAY` with a 5000 ms maximum in the precompiled sdkconfig, `lwipopts.h`), so it often misses the 5 s cap. When the server does not answer (API down) or sends no Date header, the fetch task waits for SNTP, but never past the cap, before switching Wi-Fi off.
- The cache is used (amber LED) only when the clock is known: the radio is up and SNTP answers although the API is down (scenario 24), or the clock was set earlier since power-up and a new night is pressed after the network drops (scenario 29).
- **With the hotspot off at power-up, the cache cannot be used**: no SNTP and no Date header, so its age is unknown and the budget is protective with the amber LED blinking (scenario 09). TP7's first clause ("cache used within 5 s") cannot pass after a power cycle with the network off unless an RTC is added. The test text needs restating by the team.

**Serial commands** (115200; a status line every second):

| Command | What it does |
|---|---|
| `dump` | Prints the logbook (up to 2 × 512 KB, about 90 s at 115200 baud). The loop stops meanwhile, so it is refused unless the lamp is dark and nobody is on the mark: a visitor arriving during a dump finds the path dark. Afterwards the bat sensor looks as if it had been away (FAULT for about 1 s, the room re-learned); the budget is untouched. |
| `cal [lux]` | Lamp to full; measures the lamp component on steady frames; stores lux-per-amplitude for the active unit. `lux` is the QM1587 reading at the perch, or the declared 4.0 if omitted. The FSM is paused, and this light is not charged. |
| `rebase` | Re-learns the room. The budget is unchanged. |
| `night` | New night. |

**Logbook.** `/littlefs/log.csv` gets a `row` line every second:

```
ms, state, present, level, lamp_lux, broad_delta_lux, event, lamp/event V'-proxy lux,
spent, event FMM, B, C, sky source
```

`note` lines record state changes, F6 events (start, and at the end whether the dose was charged or dropped as the room), re-baselines, scene changes, fetches (with whether the clock is set) and the sky result (including the evening temperature, logged only). The file rotates at 512 KB and one old copy is kept.

Flushing is synchronous. Lines are buffered in 4 KB of RAM and appended to flash from `loop()` every 5 s, so each flush stalls the control loop for as long as LittleFS takes. That time has not been measured; SPI flash sector erases typically take tens of milliseconds. A separate task would not remove the stall: while flash is erased or written, ESP-IDF disables the caches and the other core busy-waits. The FSM clamps its step to 200 ms, so a stall cannot jump the lamp or the budget.

### Bat sensor (AS7343) integration

`src/exhibit/spectral_as7343.cpp` is the exhibit's `SpectralSource` (`spectral_source.h`). It runs the register-level driver in `lib/as7343` and translates each driver frame into the core's `edge_spectral_frame_t` (`lib/core/spectral.h`). The driver's own README covers the chip: registers, auto-gain, bus recovery and the bring-up checks. Nothing in `lib/core` or `lib/as7343` was changed for the integration.

**Set-up.**

- **Integration: 50 ms per SMUX cycle** (`setIntegration10ms(5)`).
  - It spans whole periods of 100 Hz mains flicker (§4).
  - It matches the O1 logger's default, so O1's signal and drift figures apply to the exhibit.
  - One 18-channel frame is three cycles: nominally 150 ms, plus autozero and SMUX switching. A `static_assert` keeps the nominal frame inside `EDGE_FRAME_PERIOD_MS`.
- **Auto-gain on** (the driver's software AGC). This is safe for sep2 because every value it sees is gain-normalised (see the gain limit below).
- **Light hygiene.**
  - The PIM707's two white LEDs are held off by the driver, not the adapter. On every (re)connection it writes LED_ACT = 0 and reads it back, and it refuses a chip that does not read back off (`led-verify`). The PIM707 schematic puts the LEDs' cathodes on the AS7343's LDR pin, so with LED_ACT = 0 they cannot light.
  - The FireBeetle's own LED (GPIO2) and RGB LED (GPIO5) are switched off in `setup()`.

**Frame mapping.**

| `edge_spectral_frame_t` | Taken from `as7343::Frame` |
|---|---|
| `ch[EDGE_CH_F1 … EDGE_CH_NIR]` | `as7343::basicCounts(frame, kSlot[i])`, with `kSlot` = kF1, kF2, kFZ, kF3, kF4, kF5, kFY, kFXL, kF6, kF7, kF8, kNIR. This rearranges the driver's data-register order into the core's wavelength order (405 … 855 nm, DS001046 Fig. 7). `basicCounts` = raw / (nominal gain × integration ms per cycle), which is the core's unit. |
| `clear` | Mean of VIS1–VIS3 in basic counts (diagnostic only). |
| `t_end_ms` | The `poll()` call that received the frame: the driver completes a frame in the call that finds AVALID set. |
| `t_start_ms` | `tMs − (t_end_ms − tMs)`. The driver stamps `tMs` at the middle of the window from SP_EN to that poll. The window includes the autozero, so it is slightly wider than the three integrations; that only makes sep2's steady-frame gate stricter. |
| `valid` | `true` for every frame the chip delivered. |
| `saturated` | `analogSaturation \|\| digitalSaturation`. |

**Saturation is a flag, not a missing frame.**

- A saturated frame stays `valid`. sep2 already keeps saturated frames out of the unmixing and the baseline, but the F6 detector still sees them.
- This matters because a phone torch on the bat is the case most likely to saturate. Reporting those frames as invalid would turn a torch held for more than 1 s into FAULT (`EDGE_SENSOR_TIMEOUT_MS`), with its dose uncharged.
- Clipped counts under-read the light, so a saturated frame gives F6 a lower bound. After an analog-saturated frame the driver's auto-gain steps the gain down 4×.

**A missing chip means FAULT, then automatic recovery (TP1).**

1. The driver reports `kLost` after three consecutive failures (I2C error, AVALID timeout, or settings lost). The adapter logs the reason and reports a `valid = false` frame at once, so no stale reading is used.
2. While the chip is missing, the driver clears the bus (9 clocks + STOP) and re-probes every 0.5 s. The adapter reports `valid = false` every `EDGE_FRAME_PERIOD_MS`.
3. The core enters FAULT 1 s (`EDGE_SENSOR_TIMEOUT_MS`) after the last valid frame:
   - lamp at the floor if someone is present, otherwise off;
   - the lamp is still charged, from its last measured transfer;
   - the needle is parked on the FAULT tick.
4. On replug, the driver re-identifies the chip, re-verifies the LEDs off and reconfigures it (`kConnected`, logged). FAULT clears 1 s after the first valid frame. sep2 re-learns the room, because a different (spare) bat may have been plugged in.
   - Worst case from replug: 0.5 s probe period + one frame (about 0.17 s) + 1 s ≈ 1.7 s, under TP1's 2 s. This is arithmetic; it has not been measured.
5. A chip that resets itself (brown-out) without leaving the bus is caught by the driver's settings read-back or its AVALID timeout, and reconfigured. The gap is about 0.7 s, which is under the 1 s FAULT timeout.

All of these are logged as notes starting `bat sensor:` (found / no AS7343 (reason) / lost (reason) / connected).

**The I2C bus belongs to the bat.** The driver's bus recovery calls `Wire.end()` / `Wire.begin()` and drives SDA and SCL directly. Nothing else in the exhibit uses `Wire`. Adding another I2C device means reviewing `recoverBus()` in `lib/as7343` first.

**Power the bat from 3V3, never 5 V.**

- The PIM707 has an LDO (1.8 V for the AS7343) and MOSFET level shifters. Their host-side 10 kΩ pull-ups go to the breakout's own VIN.
- Fed from 3V3 as in §6 (3V3 → 10 Ω → 10 µF ∥ 100 nF → connector), SDA and SCL idle at 3.3 V.
- Fed from 5 V, they would pull the ESP32's pins, which are not 5 V tolerant, to 5 V.

**PWM timers and pins.**

- **Timers.** The lamp is on LEDC channel 0 (20 kHz, 11-bit) and the needle on channel 2 (50 Hz, 14-bit). In Arduino-ESP32 3.3, `ledcAttachChannel()` reuses a timer only for a channel with the same frequency and resolution; otherwise it takes a free timer (`find_matching_timer` / `find_free_timer` in `esp32-hal-ledc.c`). The lamp and the needle are therefore always on different timers: timers 0 and 1 of the high-speed group, since the lamp is attached first.
- **Pins.** A `static_assert` in `main.cpp` fails the build if the exhibit uses any pin twice. The exhibit uses I²C 21/22, lamp 25, needle 26, force 36, touch 4, buttons 13/14, selector 34/35, operator LED 18/19, and onboard LEDs 2/5.
  - The kit also uses 39 (phototransistor) and 23 (kit button).
  - The O1 logger uses 21/22, 25, 39, 2 and 5.
  - Every value in `pins.h` is distinct, so no build shares a pin between drivers.

**Things to check on Wed 7.**

- **TP6 timing has no margin at the real frame rate.**
  - The TP6 budget in §5 assumed 0.2 s frames. The F6 hold is confirmed only when a frame arrives, so with a frame period T it takes ceil(0.4 s / T) frames.
  - At T ≈ 0.17 s (nominal, not measured) that is 3 frames, or 0.51 s. The worst case becomes T + 0.51 s + one 50 ms step + (max − floor) × `EDGE_DIM_TO_FLOOR_MS` ≈ 0.17 + 0.51 + 0.05 + 0.33 = 1.06 s, against TP6's 1.0 s.
  - Measure the real frame period at bring-up (driver check 2). If TP6 fails, the levers are `EDGE_DIM_TO_FLOOR_MS` and the max − floor gap. Both are core config and a team decision.
- **Gain changes and the room baseline.**
  - Basic counts use nominal gain ratios, which the datasheet puts up to about 9% off at 2048× (Fig. 10).
  - When auto-gain changes the gain (lamp switching between very different levels, or a torch), the baseline of a bright room can shift by that fraction and read as a broadband change.
  - If O1's `gain` column shows the gain stepping in normal operation and the drift matters, lock the gain in `as7343_begin()` with `g_sensor.setGainCode(code)` at the gain O1 used.
- **Serial noise while unplugged.**
  - The core's default log level is ERROR (`CONFIG_ARDUHAL_LOG_DEFAULT_LEVEL 1`), and `esp32-hal-i2c-ng.c` logs every failed write. So each 0.5 s re-probe prints one `[E] … i2c_master_transmit failed` line. It is harmless.
  - `Serial.setDebugOutput(false)` (as the O1 logger uses) would hide it, but it would also hide every other core error, so it is left on.
- **Driver open items that also affect the exhibit** (`lib/as7343/README.md`): the assumption that AVALID clears on restart (bring-up check 2), and SMUXEN.

---

## 8. Simulator and tests

### Commands

```sh
cd sim
make test                             # everything
./build/edge_sim -v scenarios/05_torch_present.scn   # one scenario, 1 Hz trace
make CORE=/path/to/copy BUILD=/tmp/b test            # run against a modified core (absolute paths work)
```

### Scenario language

One command per line; `#` starts a comment; time advances only with `wait`, `within` and `chatter`.

| Command | Meaning |
|---|---|
| `scenario <title>` | Title, printed with the result. |
| `boot` | Power on: core init, presence learning, startup sequence (uses the current plant settings). The clock is unknown again, as after a real power-up. |
| `hand 0\|1` | Hand off / on the force sensor (raw readings go through the real debouncer). |
| `chatter <s> <period_ms>` | Toggle the raw force reading every half period for `s` seconds. |
| `lamp_lux <lux>` | Photopic lux the lamp puts on the bat at full light (default 4.0). |
| `room <lux> [white\|warm\|fluor]` | Room light at the bat and its spectrum (synthetic shapes). |
| `wobble <fraction>` | Room light ± this fraction, 3 s period (people moving). |
| `torch <lux>` | Extra cool-white light at the bat (0 = off). |
| `sensor 0\|1` | Bat sensor unplugged / connected. |
| `net ok\|down\|api_down\|truncated\|error\|late` | What a fetch meets: `ok` = the captured response; `down` = no hotspot; `api_down` = Wi-Fi joins but no server answers; `late` = a synthetic all-overcast body after the 5 s cap, inside SHOW_SKY. |
| `sntp <s>` | SNTP's first answer, seconds after Wi-Fi joins (default 2.5; lwIP waits a random 0–5 s). |
| `cache none\|fresh\|stale\|unknown_age` | What NVS holds at boot (2 h old, 30 h old, undated). |
| `selector live\|clear\|overcast` | The 3-position switch. |
| `press rebase\|night` | Operator buttons. |
| `wait <s>` | Advance time. |
| `expect <var> <op> <value>` | Assert now. |
| `within <s> <var> <op> <value>` | Advance until true; fail after `s`. |
| `remember <var>` | Store a value; use it later as `@<var>`. |

**Expectation operators and values:**

- Ops: `== != < <= > >=`.
- Values: numbers, `floor`, `max`, `bmax`, state names, source names (`LIVE CACHED RECORDED PROTECTIVE`), reasons (`ok no-data stale age-unknown no-tonight`), or `@name`.
- Vars: `state source why level present remaining spent lamp_fmm event_fmm pending budget budget_set sky_c event lamp_lux events scene_changes needle clock`. `source` and `why` describe the startup sequence's sky; they read `LIVE` / `ok` until it resolves. `sky_c` is the FSM's C, set at the hand-over. `pending` is the running event's held-aside dose.

### What the simulator models

- **Timing:** a 10 ms tick, presence sampled every tick, a spectral frame every 200 ms (180 ms integration window), and the FSM stepped every 50 ms, as in the firmware.
- **Spectra:** frames are synthesised from the placeholder endmember shapes, with 0.3% + small additive noise from a fixed-seed generator, so every run is identical.
- **Network:** Wi-Fi joins 1 s after a fetch starts. A server answers 1.5 s after the start (`ok`, `truncated`, `error`) or 6 s after it (`late`). With `api_down` no server answers and the task waits for SNTP until the cap; with `down` the join times out at the cap.
- **Clock:** unknown at every `boot`, as on the board. Only the modelled network sets it: a server's answer (its Date header) or SNTP while Wi-Fi is up during a fetch. It survives `press night`. Scenarios cannot declare a clock without a network, which is how scenario 09 used to pass on a combination the hardware cannot produce.

### Invariants checked on every step

| Invariant | Check |
|---|---|
| I1 | No crossing below the floor while present. |
| I2 | Dark within 5 s (+1 step) of the hand leaving. |
| I3 | Remaining never rises within a night. |
| I4 | At or below the floor after the dim time in YIELD. |
| I5 | At or below the floor in FAULT; FAULT gone within 1 s (+ one step) of the first valid frame. |
| I6 | C ∈ [0, 1]; protective means C = 1; stale, missing or unknown-age means C = 1. Checked against an independent oracle. |
| I7 | Per state: up never faster than the 1.5 s fade; down never faster than the 5 s fade, except YIELD and FAULT, which may use the 0.6 s dim. |
| I8 | On the FSM step after every re-baseline (button, and automatic scene change): B and `budget_set` unchanged, no event dose committed, and spending grown by no more than the lamp's own light over that step. |
| I9 | YIELD no longer than 30 s (+ frame + step). |

### Scenarios (§7.4)

| Plan item | Scenario |
|---|---|
| Quiet night | 01 |
| One visit | 02 |
| Long hold to the floor | 03 |
| Leaving during the pull-back | 04 |
| Torch while present (TP6) | 05 |
| Torch while absent | 06 |
| Sensor unplugged and replugged while present | 07 |
| Sensor absent at boot (TP1) | 08 |
| Network off at startup: no clock after power-up, protective (TP7) | 09 |
| Stale cache (Wi-Fi up, API down, clock from SNTP) | 10 |
| Undated cache, with and without a clock | 11 |
| Truncated HTTP body | 12 |
| New night during a visit | 13 |
| New visitor after the budget is exhausted | 14 |
| Force-sensor chatter | 15 |
| Room lights off then on mid-visit | 16 |
| Room lights left on → scene change (TP15) | 17 |
| Re-baseline during a visit | 18 |
| Selector changed mid-night | 19 |
| People moving in a fluorescent room, no dose (TP11) | 20 |
| API error body | 21 |
| Hand on the mark at boot | 22 |
| Near-collinear warm room changing while lit | 23 |
| Wi-Fi up, API down: SNTP dates the clock, cache used within 5 s; SNTP too slow: protective | 24 |
| Cloudier live answer after the cap, before the hand-over, tightens B | 25 |
| Bat sensor missing from boot: the floor in FAULT is still charged | 26 |
| Bat sensor lost during a torch event: YIELD holds until FAULT | 27 |
| Room lights on and left on with nobody there: remaining unchanged (TP15) | 28 |
| Network lost after a good boot: a new night uses the dated cache | 29 |

**Checking the checker.** To confirm the checks are not vacuous, deliberate bugs were planted one at a time in a scratch copy of the core and run with `make CORE=… BUILD=… test` (5 Oct 2026, after the review fixes). Every one made the suite fail:

| Planted bug | Caught by |
|---|---|
| no floor guard | I1 |
| slow YIELD dim | I4, I5 |
| FAULT latching | I5 |
| YIELD/FAULT target above the floor | I4, I5 |
| no scene change | I9 |
| stale cache used | I6 |
| budget refund | I3 |
| 10× fade | I7 |
| lamp left on when empty | I2 |
| a re-baseline commits the event it absorbed | I8 (and scenarios 17, 18, 28) |
| fade down in 4 s instead of 5 s | I7 |
| fade up in 1.2 s instead of 1.5 s | I7 |
| fade up in 2 s | scenario 02 |
| floor uncharged while the sensor is missing | scenario 26, `test_kit` |
| an invalid frame ends an event | scenario 27 |
| an answer after the cap ignored before the hand-over | scenario 25, `test_parts` |
| an undated cache replaces a dated one | `test_forecast` |

Three kit-side bugs were planted in copies of `kit_light.cpp` and `needle_servo.h` and caught by `test_kit`: lamp dose charged with the LED off, the reading always valid, and touch gated on the whole 400 ms servo hold without a move rate limit (worst release then 1000 ms, the test's cap).

**Unit tests:**

- `test_sep2`: synthetic mixtures, the fluorescent baseline, near-collinear room light against the closed-form projection, the steady gate, saturation, F6 timing, slow rises, scene changes both ways, re-baseline, and sensor return.
- `test_dose`: the unit definition, per-endmember factors, B_max sizing, the charging rules, the missing-sensor and dropped-frame estimates, adding an event's tally, and the tally / past zero.
- `test_forecast`: the captured response (C = 0.958 / 0.008, midnights, temperatures), every truncation, error and malformed bodies, tolerant layout, nulls, the budget formula, all resolution rules, the HTTP Date parser (the fixture's capture time, RFC 9110's example, a leap day, and twelve malformed or obsolete forms), and the cache-replacement rule.
- `test_parts`: presence learning, hysteresis and debounce, refusing a hand at boot, dial mapping, and startup timing (live, cap, recorded, a clearer and a cloudier answer after the cap).
- `test_kit` (C++, `fake_arduino/` stands in for the ADC, touch pad and LEDC; bench numbers are assumptions, not measurements): one minute of idle spends nothing, a lit LED is measured and charged, an unplugged phototransistor gives FAULT with the floor still charged and recovers, a saturated room gives FAULT, and touch release stays within 150 ms while a draining budget moves the needle.

---

## 9. Known limits and open issues

- **PlatformIO library finder (`lib_ldf_mode = deep+`).** The Arduino `WiFi` and `LittleFS` libraries keep their dependencies (`Network.h`, `FS.h`) behind `#if`s on sdkconfig macros that the finder cannot evaluate, so those libraries fail to build in this project. `platformio.ini` was not changed. Instead:
  - `src/exhibit/net.cpp` uses the ESP-IDF `esp_wifi` / `esp_netif` / `esp_http_client` (with `esp_crt_bundle_attach`) APIs directly;
  - the logbook uses ESP-IDF's LittleFS VFS (`esp_vfs_littlefs_register`) with C file I/O.

  Both are already linked into the Arduino core. If the team prefers the Arduino wrappers, set `lib_ldf_mode = deep` (or chain) in `platformio.ini`; that is a team decision.
- **Flash headroom.** With Wi-Fi, TLS and the AS7343 driver linked, the exhibit uses 91.5% of the default 1.25 MB app partition (about 110 KB free). A larger partition table (e.g. `board_build.partitions = huge_app.csv`) is a `platformio.ini` change for the team if more code is added.
- **AS7343 timing and gain.** The TP6 timing margin at the real frame rate, and gain steps shifting a bright room's baseline: see §7, "Things to check on Wed 7".
- **Near-collinear leak.** See §4 sep2: room-light changes while lit leak into the lamp estimate until the room is re-learned.
- **Torch dose scale.** At equal photopic light, broadband costs EDGE_SP_BROAD / EDGE_SP_AMBER ≈ 9× the lamp. With the placeholders, a 5 lux torch costs about 27 FMM per exhibit second, which is about half a clear-night budget per second. The torch beat will push the budget to or past zero unless O1/E4 numbers change this. That is the declared proxy working as specified, not a bug, but it shapes the demo.
- **Kit (FW0) measures nothing spectral** (§6).
- **One UTC offset per response.** A daylight-saving change inside the evening window would shift the window by an hour.
- **FS90 range.** FEETECH specifies about 120° for 900–2100 µs. Some vendor listings quote 180° for 500–2500 µs; the firmware uses the FEETECH range.
- **TP7 needs restating.** After a power cycle with the network off the device has no clock, so it cannot use the cache (§7). Without an RTC, TP7's first clause can pass only with Wi-Fi up and the API down, or after a new night (no power cycle).
- **Team rulings needed on behaviour the plan text leaves open** (§3):
  - TP15's "budget unchanged" is implemented as "remaining unchanged": an F6 event's dose is committed when it ends and dropped when the room is re-learned. Consequence: the needle moves when the torch is taken away, not while it is held, and the re-baseline button forgives an event in progress. If the team rules that events are charged as they happen, `charge()` in `budget_fsm.c` puts the event part straight onto the tally, and room lights left on then drain far past zero (scenario 28 with that change, placeholder numbers: remaining falls from 20.6 to about −3,260 FMM over the 30 s).
  - Fades are rate-based, so TP3 is timed from maximum.
  - F4's interpolation is perceptual, not lux-linear (0.49 vs 0.59 of maximum light at half budget).
- **Logbook flushes block the loop** for an unmeasured time every 5 s (§7).
- **Touch coupling** from the servo into the electrode is unverified; `EDGE_NEEDLE_QUIET_MS` is a placeholder.
- **Unverified on hardware.** Nothing here has run on a board yet, including the AS7343 integration (§7). Every hardware constant marked PLACEHOLDER needs its bench test (§5).

---

## 10. Reuse from the earlier booth firmware

These patterns were taken from the earlier `firmware/main/fsm.c` and `tools/fsm_sim.c`. Those superseded files were retired during the 8 October cleanup; [the version history](../../VERSION_HISTORY.md) records their lineage:

- policy in pure C with a host simulator;
- `enter()` and the rate-based `ramp()`;
- the floor invariant;
- the 1.5 s / 5 s fades;
- "fail dark".

Two things were not carried over:

- `led.c`'s 12-bit / 5 kHz LEDC is now 11-bit / 20 kHz (§4).
- `servo.c`'s pulse range, whose comment claimed a bench measurement that never happened. The FS90 range comes from the manufacturer's specification instead.

---

## 11. Sources consulted for this firmware (5 Oct 2026)

- **DFRobot, FireBeetle 2 ESP32-E (DFR0654) wiki**, wiki.dfrobot.com/dfr0654 (pin table: input-only 34–39, ADC1 on 36/39, IO2 LED, IO5 WS2812, IO27 user button, USB pins 0/1/3), and the board variant `variants/dfrobot_firebeetle2_esp32e/pins_arduino.h` in Arduino-ESP32 3.3.11.
- **ams OSRAM, AS7343 datasheet** (look.ams-osram.com/m/5f2d27fff9a874d2/original/AS7343-14-Channel-Multi-Spectral-Sensor.pdf): channel peak wavelengths and FWHMs (F1 405 … NIR 855 nm), VIS and flicker channels, 14 channels from 380–1000 nm. The register-level facts the integration relies on are cited in `lib/as7343/README.md`.
- **Pimoroni, AS7343 breakout (PIM707)** product page, shop.pimoroni.com/products/as7343-breakout (I²C address 0x39, two on-board illumination LEDs, 3.3 V or 5 V, 19 × 20 × 5 mm), and its schematic, `AS7343_breakout_schematic.pdf` linked from that page, dated 13/06/2024 (1.8 V LDO; MOSFET I²C level shifters with 10 kΩ pull-ups to VIN on the host side; LED cathodes on the AS7343 LDR pin). Both opened 5 Oct 2026.
- **Open-Meteo API documentation** (open-meteo.com/en/docs):
  - `timeformat=unixtime` gives GMT+0 epochs; apply `utc_offset_seconds` for daily dates;
  - `cloud_cover` is the instantaneous total cloud cover in %;
  - errors return `{"error": true, "reason": …}` with HTTP 400.
- **Open-Meteo response** captured once on 2026-10-05 08:01:41 UTC: `sim/fixtures/openmeteo_aifs_20261005.json`, also embedded as the recorded nights.
- **FEETECH FS90**, via Pololu product 2818: about 120° for 900–2100 µs pulses. The Core Electronics CE09376 listing's 180° / 500–2500 µs was noted but not used.
- **Arduino-ESP32 3.3.11 sources** (installed package):
  - LEDC timer allocation (`find_matching_timer` / `find_free_timer` in `esp32-hal-ledc.c`) and the full-duty special case;
  - `TwoWire::end()` / `begin()` freeing and reallocating their buffers (`Wire.cpp`), so the driver's bus clear every 0.5 s while the bat is unplugged does not leak;
  - `esp32-hal-i2c-ng.c` error logging on a failed write, and `CONFIG_ARDUHAL_LOG_DEFAULT_LEVEL 1` in the precompiled `sdkconfig.h`;
  - the new touch driver's `touch_value_t`;
  - `NetworkClientSecure` handshake units;
  - the embedded `x509_crt_bundle` in the core's mbedTLS library;
  - `configTime` in `esp32-hal-time.c`.
- **RFC 9110, HTTP Semantics** (rfc-editor.org/rfc/rfc9110), opened 5 Oct 2026: §6.6.1 (an origin server with a clock must send Date in 2xx, 3xx and 4xx responses) and §5.6.7 (IMF-fixdate, its example `Sun, 06 Nov 1994 08:49:37 GMT`, and the two obsolete forms a recipient is asked to accept).
- **ESP-IDF 5.5.5 sources and docs** (installed `framework-espidf` and precompiled `framework-arduinoespressif32-libs`):
  - `esp_http_client.h` / `esp_http_client.c`: `HTTP_EVENT_ON_HEADER` is dispatched once per complete header with `header_key` and `header_value`;
  - `lwipopts.h` and the precompiled `sdkconfig.h`: `CONFIG_LWIP_SNTP_STARTUP_DELAY` 1 and `CONFIG_LWIP_SNTP_MAXIMUM_STARTUP_DELAY` 5000, giving `SNTP_STARTUP_DELAY_FUNC = LWIP_RAND() % 5000`;
  - `sdkconfig.h`: `CONFIG_MBEDTLS_HAVE_TIME` set, `CONFIG_MBEDTLS_HAVE_TIME_DATE` not set;
  - "Concurrency Constraints for Flash on SPI1" (docs.espressif.com, ESP-IDF v5.5, ESP32): the caches are disabled while flash is read, written or erased, other tasks are suspended and the other core polls in a busy loop.
- **Arduino-ESP32 3.3.11** `esp32-hal-adc.c`: 11 dB is the default attenuation, and `analogSetPinAttenuation()` on a pin not yet set up by `analogRead()` only logs "Pin is not configured as analog channel".
- **H. Hinnant, "chrono-Compatible Low-Level Date Algorithms"** (howardhinnant.github.io/date_algorithms.html): `days_from_civil`, used in `http_date.c`.
- **Open-Meteo response headers**, `curl -I` on 5 Oct 2026: the Date header is IMF-fixdate.
- **Plan values** used as declared or placeholder inputs (S/P ratios, FMM, m, fades, thresholds): `REVISION_3_PLAN.md` §3, §4, §7, §9.
