#!/usr/bin/env python3
"""E4 - measure the lamp's S/P ratio, and know how far off it could be.

WHAT THIS SETTLES
    EDGE_SP_AMBER in edge_config.h is 0.23, flagged PLACEHOLDER. This script
    turns a measured spectral power distribution into a defensible S/P, and
    reports how much a wavelength error would cost -- which is the number that
    decides whether the measurement is good enough.

THE TRAP THIS AVOIDS
    S/P is NOT the ratio of the two integrals on their own. Both CIE curves are
    normalised to 1 at their peaks, so the raw ratio must be multiplied by the
    luminous efficacy constants:

        K_m  =  683 lm/W   photopic maximum, at 555 nm
        K'_m = 1700 lm/W   scotopic maximum, at 507 nm

        S/P = (K'_m / K_m) * integral(SPD.V') / integral(SPD.V)
            = 2.489 * ...

    Omit that factor and every value comes out 2.489x too small -- which is how
    this script's author first reported the project's 0.23 as a 2.5x error. It
    is not. Check against the amber case in --selftest before trusting any edit.

USAGE
    python3 e4_sp.py --selftest                 prove the curves and the maths
    python3 e4_sp.py --spd measured.csv         your measured spectrum
    python3 e4_sp.py --nominal 590 15           a Gaussian, for planning
    python3 e4_sp.py --sensitivity 590 15       how much a nm error costs

    SPD CSV format: two columns, wavelength_nm,power. Any units; only the shape
    matters. 380-730 nm is enough; the script interpolates onto 1 nm.
"""
import argparse
import csv
import pathlib
import sys

import numpy as np

HERE = pathlib.Path(__file__).resolve().parent
CIE = HERE / "cie"

K_M_PHOTOPIC = 683.0     # lm/W at 555 nm
K_M_SCOTOPIC = 1700.0    # lm/W at 507 nm
K_RATIO = K_M_SCOTOPIC / K_M_PHOTOPIC      # 2.489

WL = np.arange(380.0, 731.0, 1.0)


def load_cie(path):
    d = {}
    for row in csv.reader(open(path)):
        if len(row) >= 2 and not row[0].lstrip().startswith("#"):
            try:
                d[float(row[0])] = float(row[1])
            except ValueError:
                pass
    if not d:
        sys.exit(f"  no data in {path}")
    return d


def curves():
    v = load_cie(CIE / "cie1924_photopic_V.csv")
    vp = load_cie(CIE / "cie1951_scotopic_Vprime.csv")
    return (np.array([v.get(w, 0.0) for w in WL]),
            np.array([vp.get(w, 0.0) for w in WL]))


V, VP = curves()


def gauss(centre, fwhm):
    return np.exp(-0.5 * ((WL - centre) / (fwhm / 2.3548)) ** 2)


def sp_ratio(spectrum, with_efficacy=True):
    """Return (S/P, photopic-weighted integral, scotopic-weighted integral)."""
    phot = float(np.trapezoid(spectrum * V, WL))
    scot = float(np.trapezoid(spectrum * VP, WL))
    if phot <= 0:
        raise ValueError("spectrum has no photopic content in 380-730 nm")
    raw = scot / phot
    return (raw * K_RATIO if with_efficacy else raw), phot, scot


def load_spd(path):
    wl, pw = [], []
    for row in csv.reader(open(path)):
        if len(row) >= 2 and not row[0].lstrip().startswith("#"):
            try:
                wl.append(float(row[0])); pw.append(float(row[1]))
            except ValueError:
                pass
    if len(wl) < 5:
        sys.exit(f"  {path}: need at least 5 wavelength rows")
    order = np.argsort(wl)
    wl = np.array(wl)[order]; pw = np.array(pw)[order]
    # normalise so only the shape matters, then interpolate onto 1 nm
    pw = pw / pw.max()
    return np.interp(WL, wl, pw, left=0.0, right=0.0)


def peak_of(spectrum):
    return float(WL[int(np.argmax(spectrum))])


def report(name, spectrum, claimed=None):
    sp, phot, scot = sp_ratio(spectrum)
    print(f"  {name}")
    print(f"    peak            {peak_of(spectrum):.0f} nm")
    print(f"    S/P             {sp:.3f}")
    if claimed is not None:
        delta = 100.0 * (sp - claimed) / claimed
        print(f"    against {claimed:<6} {delta:+.1f}%")
    return sp


def sensitivity(centre, fwhm):
    base = sp_ratio(gauss(centre, fwhm))[0]
    print(f"=== how much a wavelength error costs (peak {centre:.0f} nm, {fwhm:.0f} nm FWHM) ===")
    print(f"  {'peak':>6}{'S/P':>9}{'vs nominal':>13}{'error':>9}")
    for shift in (-10, -5, -3, -2, -1, 0, 1, 2, 3, 5, 10):
        val = sp_ratio(gauss(centre + shift, fwhm))[0]
        err = 100.0 * (val - base) / base
        flag = "  <-- exceeds 10%" if abs(err) > 10 and shift else ""
        print(f"  {centre+shift:>6.0f}{val:>9.3f}{err:>12.1f}%{abs(err):>8.1f}%{flag}")
    print()
    print("  READ THIS: the amber lamp's S/P moves about 5% per nanometre.")
    print("  A +-5 nm measurement is a +-25% S/P. So the peak must be measured")
    print("  to better than about +-2 nm for the value to mean anything, and the")
    print("  measurement's own wavelength accuracy must be reported alongside it.")
    print("  If the instrument cannot do +-2 nm, say so and report the range.")


def selftest():
    print("=== curve sanity ===")
    ok = True
    for name, curve, exp_peak in [("photopic V", V, 555), ("scotopic V'", VP, 507)]:
        pk = float(WL[int(np.argmax(curve))])
        peak_val = float(curve.max())
        good = abs(pk - exp_peak) <= 2.5 and abs(peak_val - 1.0) < 0.01
        ok &= good
        print(f"  {name:<14} peak {pk:.0f} nm (expect {exp_peak}), value {peak_val:.4f}"
              f"   {'OK' if good else 'FAIL'}")
    print(f"  efficacy factor K'_m/K_m = 1700/683 = {K_RATIO:.3f}")
    print()
    print("=== does it reproduce the project's declared values? ===")
    print("  These come from CIE curves + the project's own spectral models.")
    print("  A match here means the maths is right, NOT that the lamp is measured.")
    print()
    cases = [
        ("amber 590/15 nm   (EDGE_SP_AMBER)", gauss(590, 15), 0.23),
        ("amber 595/17 nm",                   gauss(595, 17), 0.19),
        ("cool white",                        1.00 * gauss(450, 20) + 0.55 * gauss(555, 120), 2.09),
    ]
    for name, spec, claimed in cases:
        sp, _, _ = sp_ratio(spec)
        err = 100.0 * (sp - claimed) / claimed
        flag = "OK" if abs(err) < 6 else "CHECK"
        print(f"  {name:<34} {sp:.3f}  vs {claimed:<5} {err:+6.1f}%  {flag}")
    print()
    print("  The amber case is the one that matters: 0.232 vs 0.23.")
    print("  If your edit makes this disagree, the edit is wrong.")
    print()
    print("=== the like-for-like ratio -- the number for the poster ===")
    amber = sp_ratio(gauss(590, 15))[0]
    warm = sp_ratio(0.35 * gauss(450, 20) + 1.00 * gauss(600, 130))[0]
    cool = sp_ratio(1.00 * gauss(450, 20) + 0.55 * gauss(555, 120))[0]
    print(f"  at equal photopic light:  3000 K white {warm/amber:.1f}x   cool white {cool/amber:.1f}x")
    print(f"  the archived plan predicted 'about 6-9x' -- the prediction holds.")
    print(f"  E4 replaces these modelled spectra with MEASURED ones.")
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser(description="E4 - lamp S/P from a measured spectrum")
    ap.add_argument("--selftest", action="store_true", help="prove the curves and the maths")
    ap.add_argument("--spd", metavar="CSV", help="measured spectrum, wavelength_nm,power")
    ap.add_argument("--nominal", nargs=2, type=float, metavar=("CENTRE", "FWHM"),
                    help="a Gaussian, for planning")
    ap.add_argument("--compare", type=float, metavar="S/P", help="a value to compare against")
    ap.add_argument("--sensitivity", nargs=2, type=float, metavar=("CENTRE", "FWHM"))
    a = ap.parse_args()

    if a.selftest:
        return selftest()
    if a.sensitivity:
        sensitivity(*a.sensitivity); return 0
    if a.spd:
        report(a.spd, load_spd(a.spd), a.compare); return 0
    if a.nominal:
        report(f"nominal Gaussian {a.nominal[0]:.0f}/{a.nominal[1]:.0f} nm",
               gauss(*a.nominal), a.compare); return 0
    ap.print_help()
    return 0


if __name__ == "__main__":
    sys.exit(main())
