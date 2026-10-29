# E4 — measure the lamp's spectrum, and settle `EDGE_SP_AMBER`

**What this settles.** `EDGE_SP_AMBER = 0.23` in `edge_config.h` carries the comment *"PLACEHOLDER until E4"*. So does `EDGE_LAMP_PHOT_LUX_AT_MAX = 4.0`, and the `shape`, `phot_per_amp` and `sp_ratio` fields in `calib.h`. **E4 replaces all of them with measured numbers.**

**Read [WHY_E4.md](WHY_E4.md)** for why it matters. This document is the procedure.

**Time:** about half a day, plus a warm-up hour. **Hardware:** everything is already on the bench.

---

## 1. What E4 produces

| Output | Goes into | Currently |
|---|---|---|
| The lamp's S/P ratio | `EDGE_SP_AMBER` | 0.23, placeholder |
| The lamp's 12-channel `shape` | `calib.h` | from an analytic model |
| Photopic lux per unit amplitude | `calib.h` via the `cal` serial command | unset |
| The broadband endmember's S/P | `calib.h` | literature value |
| **The like-for-like ratio** | **the poster** | "predicted 6–9×" |

**The last one is the number an audience actually understands:** *at the same brightness, a white light costs this many times more than an amber one.*

---

## 2. The trap: **circular E4**

The archived plan names this risk and it is the one thing that can waste the whole exercise.

> **If the lamp's spectrum is derived from the project's own two-endmember model, and then used to validate that model, nothing has been demonstrated.**

**The rule:** the spectrum must come from an instrument *outside* `sep2.c`. In order of preference:

1. **A borrowed spectrometer** — 1 nm resolution. Best.
2. **A DIY grating + phone camera** — workable, but **its wavelength accuracy must be measured and reported**, because of §4.
3. **The manufacturer's datasheet SPD** — acceptable if the part is binned. **This one is not:** the plan records it as *"generic part with no binning: measure the peak on arrival."*

**Never:** a spectrum produced by the firmware's own model.

**The tag's filter channels cannot resolve a 590 nm peak.** The nearest channels are **555 nm (FY)** and **600 nm (FXL)**, 45 nm apart, and the lamp is only 15 nm wide. Those channels can only check that a lamp matches a spectrum measured by another instrument.

---

## 3. Why ±2 nm is the whole game

The amber lamp's S/P moves about **5% per nanometre**:

| peak | S/P | vs 590 nm |
|---|---|---|
| 580 nm | 0.367 | **+58%** |
| 585 nm | 0.293 | +26% |
| 588 nm | 0.255 | +10% |
| **590 nm** | **0.232** | — |
| 592 nm | 0.211 | −9% |
| 595 nm | 0.182 | −21% |
| 600 nm | 0.143 | −38% |

**So a ±5 nm measurement is a ±25% S/P.** The peak must be measured to **better than ±2 nm**, and **the instrument's own wavelength accuracy must be stated in the write-up.** If it cannot do ±2 nm, do not pretend — report the range the error implies.

Reproduce this table any time with:

```bash
python3 sim/tools/e4_sp.py --sensitivity 590 15
```

---

## 4. Procedure

### Step 0 — Verify the analysis tool first

```bash
python3 sim/tools/e4_sp.py --selftest
```

**It must print `amber 590/15 nm ... 0.232 vs 0.23 ... OK`.** If it does not, the tooling is wrong and no measurement will save it.

**Note what it is doing.** S/P is *not* the bare ratio of the two integrals — both CIE curves are normalised to 1 at their peaks, so the ratio must be multiplied by the luminous efficacy constants:

```
    S/P  =  (K'_m / K_m) x integral(SPD.V') / integral(SPD.V)
          =  2.489 x ...
```

**Omit that factor and every value comes out 2.489× too small** — which is exactly how this project's own 0.23 was once wrongly reported as a 2.5× error. It is not an error.

### Step 1 — Dark reference

All lights off, lamp off. Record the 12 filter channels + clear, 100 averages.

**Everything measured later is subtracted from this.** A dark reading taken in a lit room is worthless.

### Step 2 — Warm up, and measure both ends

**Run the lamp for 15 minutes before measuring.** An LED's emission peak shifts with junction temperature — several nanometres between cold and hot, which §3 shows is worth tens of percent of S/P.

**Measure at t = 0 (cold) and t = 15 min (hot), and report both.** The difference *is* the uncertainty, and reporting it is worth more than pretending it is not there.

### Step 3 — Geometry

- Lamp and sensor on a **single axis**, square to each other.
- Distance **measured with calipers**, not paced out: at **250 mm**, matching the design's lamp-to-bat geometry.
- **Inverse square means a 5% distance error is a 10% level error.** Write the distance down.
- **Black card around the sensor** so it sees the lamp and nothing else.

### Step 4 — Capture the spectrum

- **Spectrometer:** record 380–780 nm at ≤5 nm. Save as CSV.
- **Phone grating:** same range; **record how you calibrated the wavelength scale.**
- Note the lamp's drive current. The plan specifies **165–185 mA**; measure at the actual operating point, not at a convenient one.

### Step 5 — Capture the filter channels

At the same distance and current:

- All 12 filtered channels + clear, **100 averages**
- **Check for saturation** at both analog and digital — the driver already flags it. If saturated, increase the distance or reduce the integration time and correct the geometry afterwards.

### Step 6 — Compute S/P

```bash
python3 sim/tools/e4_sp.py --spd measured_lamp_590nm.csv --compare 0.23
```

**Record:** the S/P, the peak wavelength, the FWHM, **the instrument's wavelength accuracy**, and the cold/hot spread.

### Step 7 — Verify the lamp against the spectrum

Convolve the measured SPD with the filter-channel response curves and compare the **predicted channel ratios** with the measured ones.

- **They agree** → the SPD is right, use it.
- **They disagree** → the lamp differs from the spectrum, or the lamp is not the part ordered. **Stop and find out which.** This is the check that catches a mislabelled unit.

**This step checks the lamp against the measured spectrum.** It does not produce the spectrum; it stops you trusting a wrong one.

### Step 8 — Absolute calibration

Set `phot_per_amp` with the **`cal` serial command** (see `src/exhibit/bench_cal.h`).

Cross-check against a lux meter if one is available. **If the two disagree, report the disagreement** — the archived plan flags *"lux meter mismatch"* as a known risk, and a reported mismatch costs nothing while an unreported one is a hole.

### Step 9 — The white comparison

Repeat Steps 4–6 for:

- **the phone torch** — the visitor's own light
- **a 3000 K LED** — a domestic source

Then compute the **like-for-like ratio at equal photopic light**. This is the poster number. The CIE curves currently predict **4.6× (3000 K)** and **9.3× (cool white)** against amber; the archived plan predicted *"about 6–9×"* from literature values. **E4 replaces the prediction with a measurement.**

---

## 5. What to write down

| Field | Why |
|---|---|
| Date, operator | every other measurement in this project has one |
| **The instrument, and its wavelength accuracy** | decides whether the S/P means anything (§3) |
| Distance, in mm | inverse square |
| Drive current, in mA | spectrum depends on it |
| Cold and hot peak, and cold/hot S/P | the thermal uncertainty |
| Peak, FWHM | the lamp's identity |
| **S/P, and the range the error implies** | the deliverable |
| Predicted vs measured channel ratios | the cross-check (§7) |
| Lux-meter cross-check, if used | the known risk |

**Then:** update `EDGE_SP_AMBER` and the `calib.h` fields, **replace the PLACEHOLDER comments with the measurement date**, add a row to `CITATIONS_LOG.md` for the instrument and any datasheet, and add a `BUILD_LOG.md` entry.

---

## 6. What would make E4 fail

| Failure | Symptom | Prevention |
|---|---|---|
| **Circular E4** | the spectrum came from the firmware's own model | §2 — an outside instrument, always |
| Spectrum good, lamp wrong | channel ratios disagree in Step 7 | Step 7 exists for this |
| **Wavelength error** | S/P off by tens of percent, silently | §3 — measure peak to ±2 nm and state the accuracy |
| Cold measurement | the value depends on when you measured | Step 2 — 15-minute warm-up, report both |
| Saturated sensor | channel readings pinned, ratios meaningless | Step 5 — the driver already flags it |
| Lit-room dark reading | everything offset | Step 1 — lights off, verified |
| Quoting the placeholder | 0.23 looks like a measurement | it is not, until this procedure has run |

---

## 7. What E4 does *not* settle

- **It does not make the proxy valid.** S/P uses the **CIE V′ human rod curve** as a declared proxy; there is no published spectral sensitivity for *N. gouldi*. E4 makes the *lamp* known, not the *animal*.
- **It does not settle the full-SPD question.** [BUILD_SPEC.md](../../../../BUILD_SPEC.md) §10.3 records that Longcore (2020) prefers weighting the whole spectrum over any single ratio. E4 produces the spectrum, so that path stays open — but E4 alone does not take it.
- **It does not measure any bat.** Exposure is not welfare; nothing here changes that.
