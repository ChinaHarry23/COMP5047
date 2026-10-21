# Why E4 exists

**Read this before [E4_PROTOCOL.md](E4_PROTOCOL.md).** That document tells you how to run the test. This one tells you what it is for.

---

## 1. The one-sentence version

**E4 decides whether "amber is cheaper for bats" is a measurement or a claim.**

---

## 2. The problem

The project's central argument is that **light quality matters as much as light quantity** — that at the same brightness, an amber lamp and a white one do not cost a bat the same.

That argument rests on a single number in `edge_config.h`:

```c
#define EDGE_SP_AMBER  0.23f   /* PLACEHOLDER until E4 */
```

**A placeholder.** If it is wrong, three things break at once:

- the **budget** is sized wrong, because `B_max` is computed from it
- the **cost difference** between amber and white is wrong
- and the **poster number** — the one sentence an audience remembers — is wrong

**Nothing else in the project depends on so much from so little.**

---

## 3. Why it cannot simply be looked up

The obvious move is to take 0.23 from a table and move on. The project's own plan records why that is not enough:

> *Generic part with no binning: **measure the peak on arrival.***

The lamp is a cheap 3 W star with **no binning**, meaning the manufacturer does not guarantee its peak wavelength. Two units from the same bag can differ.

**And the sensitivity is brutal.** S/P moves about **5% per nanometre**:

| peak | S/P | error |
|---|---|---|
| 585 nm | 0.293 | **+26%** |
| **590 nm** | **0.232** | — |
| 595 nm | 0.182 | **−21%** |

**So a lamp 5 nm off spec is a 25% error in the project's central number** — invisible, silent, and baked into every budget the exhibit ever computes.

---

## 4. Why the sensor cannot do it alone

A reasonable objection: the project *has* a 14-channel spectrometer. Why not use it?

Because of where the lamp sits:

```
    channel:   F5(550)   FY(555)          FXL(600)
                  │         │                  │
                  │         │        ┌─────────┴────────┐
                  │         │        │  the lamp        │
                  │         │        │  590 nm, 15 nm   │
                  └─────────┴────────┴──────────────────┘
                            45 nm between channels,
                            and the lamp is 15 nm wide
```

**The lamp falls between two channels.** The AS7343 can say *"narrowband, around 590–600 nm, not white"* — which is exactly what `sep2.c` needs — but it cannot pin the peak to ±2 nm.

**So the division of labour is:**

- **An outside instrument** produces the spectrum.
- **The AS7343 verifies** that the lamp matches it.

---

## 5. The trap

**Circular E4.** If the spectrum is derived from the project's own two-endmember model, and then used to validate that model, **nothing has been demonstrated** — the result is guaranteed to agree with itself.

The archived plan names this risk explicitly. **The fix is one rule:** the spectrum comes from an instrument outside `sep2.c`.

---

## 6. What E4 buys

| | Before | After |
|---|---|---|
| `EDGE_SP_AMBER` | a placeholder | a measured value, with a date and an instrument |
| The lamp's `shape` | an analytic model | the real thing |
| `phot_per_amp` | unset | set on the bench |
| The poster number | *"predicted about 6–9×"* | **measured** |
| The write-up | *"we assumed"* | *"we measured, to this accuracy"* |

**The last two rows are the point.** E4 is what lets the report say *how it knows* — which is the question the highest band of the rubric asks, in those words.

---

## 7. The 20-minute version

If a full day is not available:

1. Run `e4_sp.py --selftest`. **Two minutes.** It must reproduce 0.232 vs 0.23.
2. Warm the lamp **15 minutes**. Use the time to set up the geometry.
3. Measure the peak with whatever is available — grating, datasheet, or a borrowed spectrometer.
4. **Report the peak and the instrument's accuracy**, and use `--sensitivity` to state what that accuracy means for S/P.
5. **Write both numbers down, with the date.**

**Step 4 is the one that cannot be skipped.** An honest ±25% beats a false ±2%.

---

## 8. What E4 does not do

- **It does not validate the proxy.** The S/P uses the CIE V′ **human rod** curve as a declared proxy. No spectral sensitivity is published for *N. gouldi*. E4 makes the lamp known, not the animal.
- **It does not measure a bat.** Exposure is not welfare.
- **It does not settle §10.3.** Longcore (2020) prefers weighting the whole spectrum over any single ratio. E4 produces a spectrum, so that path stays open — but E4 alone does not walk it.
