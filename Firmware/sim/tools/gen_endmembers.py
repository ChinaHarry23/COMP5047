#!/usr/bin/env python3
"""Generate PLACEHOLDER AS7343 endmember signatures for lib/core/calib.c and the
simulator's synthetic room-light shapes (sim/plant_shapes.h).

Nothing here is measured. Every spectrum is an analytic stand-in, used only
until E0/E4 replace the tables with signatures captured from the real amber
star, a phone torch and the presentation room's lights.

Model (all deliberate simplifications, stated so they can be challenged):
  * Each AS7343 channel is a unit-peak Gaussian at the datasheet's typical
    peak wavelength with the datasheet's typical FWHM. Real channels differ in
    responsivity by up to ~10x and have side lobes; E0 captures that.
  * Amber star: Gaussian at 590 nm, FWHM 15 nm (the "590/15 nm" case in the
    plan's S/P table).
  * Broadband (phone torch / cool-white LED): blue pump at 450 nm (FWHM 20 nm)
    plus a broad phosphor hump at 555 nm (FWHM 120 nm).
  * Photopic weighting V(lambda): Gaussian approximation, peak 559 nm. It is
    used only to set the ratio of lux-per-count between the two endmembers.

Outputs: unit-sum signatures (the shape sep2 unmixes against) and the ratio
(lux per unit amplitude, broadband) / (lux per unit amplitude, amber).

Usage:  python3 sim/tools/gen_endmembers.py
"""
import numpy as np

# AS7343 typical peak wavelength and FWHM (ams AS7343 datasheet, optical
# characteristics table), in wavelength order. VIS (clear) and FD (flicker) are
# not used for separation.
CHANNELS = [
    ("F1", 405, 30), ("F2", 425, 22), ("FZ", 450, 55), ("F3", 475, 30),
    ("F4", 515, 40), ("F5", 550, 35), ("FY", 555, 100), ("FXL", 600, 80),
    ("F6", 640, 50), ("F7", 690, 55), ("F8", 745, 60), ("NIR", 855, 54),
]

WL = np.arange(350.0, 1001.0, 1.0)  # nm


def gauss(centre, fwhm):
    sigma = fwhm / 2.3548
    return np.exp(-0.5 * ((WL - centre) / sigma) ** 2)


def channel_signal(spectrum):
    return np.array([np.trapezoid(spectrum * gauss(p, w), WL) for _, p, w in CHANNELS])


def photopic(spectrum):
    v = 1.019 * np.exp(-285.4 * (WL / 1000.0 - 0.559) ** 2)
    return np.trapezoid(spectrum * v, WL)


SOURCES = {
    "amber_590": gauss(590, 15),
    "cool_white": 1.0 * gauss(450, 20) + 0.55 * gauss(555, 120),
    # Simulator-only room lights (synthetic, never used by the firmware):
    "warm_white": 0.35 * gauss(450, 20) + 1.0 * gauss(600, 130),
    "triphosphor": (0.30 * gauss(436, 10) + 0.80 * gauss(546, 10)
                    + 1.00 * gauss(611, 10) + 0.05 * gauss(560, 200)),
}


def summary(name):
    s = SOURCES[name]
    counts = channel_signal(s)
    shape = counts / counts.sum()
    lux_per_amp = photopic(s) / counts.sum()
    return shape, lux_per_amp


def c_array(values):
    return ", ".join(f"{v:.5f}f" for v in values)


def main():
    amber_shape, amber_lpa = summary("amber_590")
    print("/* lib/core/calib.c */")
    for name in ("amber_590", "cool_white"):
        shape, lpa = summary(name)
        print(f"/* {name}: */ {{ {c_array(shape)} }},")
        print(f"/*   lux-per-amplitude relative to amber: {lpa / amber_lpa:.4f} */")
    print()
    print("/* sim/plant_shapes.h */")
    for name in ("warm_white", "triphosphor"):
        shape, lpa = summary(name)
        print(f"/* {name}: */ {{ {c_array(shape)} }},")
        print(f"/*   lux-per-amplitude relative to amber: {lpa / amber_lpa:.4f} */")
    a, w = summary("amber_590")[0], summary("cool_white")[0]
    cos = a @ w / (np.linalg.norm(a) * np.linalg.norm(w))
    print(f"\n/* cosine(amber, cool_white) = {cos:.4f} */")


if __name__ == "__main__":
    main()
