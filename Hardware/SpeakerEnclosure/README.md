# Speaker enclosure for the Core Electronics CE08483 (Makerverse 8 Ω 5 W enclosed speaker)

A two-part, support-free, 3D-printable housing for the [Enclosed Speaker - 8 Ohm, 5 Watt](https://core-electronics.com.au/makerverse-enclosed-speaker-8-ohm-5-watt.html)
(the same moulding is sold by Waveshare as "8Ω 5W Speaker"). Outer size **125 × 54 × 31 mm**.

![assembly](img/assembly.png)

| | |
|---|---|
| ![exploded](img/exploded.png) | ![back](img/assembly_back.png) |

The speaker is a sealed unit already, so this housing is for protection, looks, mounting and
cable strain relief. It clamps the speaker by its 100 × 45 outline and its two flat faces only:
the back face sits on four foam-padded standoffs in the **tray**, and the **bezel** (a 4 mm frame
with an integral hex grille) is pulled down onto the speaker's flat front land *and* onto the
tray rim by four M3 screws. The foam supplies the preload, the rim is a hard stop, so the screws
cannot crush anything and the speaker cannot rattle. Nothing touches the mounting ears, so the
dimensions that were hard to confirm from the product photos (ear plane, ear hole size, exact
cone positions) do not matter; see [SPEAKER_SPEC.md](SPEAKER_SPEC.md) for what was measured and
how confident each number is.

## Files

| File | What |
|---|---|
| `speaker_enclosure.scad` | The parametric model. Open in OpenSCAD, pick `part` in the Customizer. |
| `speaker_ce08483.scad` | Dummy of the speaker, used by the assembly view and the fit checks. |
| `stl/` | Rendered STLs for the default parameters, ready to slice. |
| `img/` | Preview renders. |
| `render.sh` | Re-renders `stl/` and `img/` from the `.scad`. |
| `check_model.py` | Mesh checks: watertight, bed size, interference between parts and speaker. |
| `SPEAKER_SPEC.md` | Speaker dimensions, sources and confidence. |

## Parts and printing

All parts print flat with **no supports**. 0.4 mm nozzle, 0.2 mm layers, PLA or PETG.

| Part | Size | Bed face | Settings | Approx. |
|---|---|---|---|---|
| `tray` | 125.2 × 54.2 × 27 | back face down, pocket up | 4 perimeters, 20 % infill, 4 top/bottom layers | 60 g, 4–5 h |
| `bezel` | 125.2 × 54.2 × 4 | **front (cosmetic) face down** | 3 perimeters, 100 % infill (it is only 4 mm), smooth or textured PEI for the face | 20 g, 1–1.5 h |
| `coupon` | 105 × 50 × 6 | either face | 2 perimeters | 4 g, 12 min |

Print the **coupon first**: it is a 6 mm slice of the pocket with the locating ribs. The speaker
should drop through it with light finger pressure. If it does not, shave the eight 0.3 mm ribs
with a knife, or raise `clr_xy` by 0.1–0.2 and re-render. If it rattles, lower `clr_xy`.

Turn **off** brims and "elephant foot compensation" larger than 0.3 mm (the model already
chamfers the bed edges by 0.3). Keep "hole horizontal expansion" at 0; the grille is sized for
a typical 0.3 mm undersize and the model asserts ≥ 40 % open area over each cone at that size.

Bridges: the longest is 8 mm (the foot-recess ceilings and the zip-tie saddle), well inside what
any printer bridges unaided. Overhangs: every chamfer and countersink is 45°.

## Hardware

| Qty | Item | Where |
|---|---|---|
| 4 | **M3 × 10 countersunk** (DIN 7991 / ISO 10642), steel or nylon | bezel → tray. Self-tap into the Ø2.5 pilots; 6 mm engagement. |
| 4 | self-adhesive foam pads, **10 × 8 × 2 mm** (cut from 2 mm EVA / neoprene tape) | on the four standoff pads in the tray |
| 1 | 2.5 mm zip tie (or tie an overhand knot in the lead pair) | strain relief on the saddle in the cable bay |

Options: `fastener = "insert"` gives Ø4.0 × 5.8 bores for M3 heat-set inserts (same screws);
`screw_head = "pan"` gives Ø6.4 counterbores for pan/button heads. If a self-tapped pilot ever
strips, run an M3 × 30 through the Ø3.8 rear bore with a nut on the back.

For mounting (choose one): 2 × #6 / M3.5–M4 pan-head screws + plugs (keyholes); 2–4 × M3 × 20
from behind a ≤ 3 mm panel (hidden, through the rear bores into the same pilots); 2 × 4.8 mm zip
ties or a 2 mm strap (strap slots); 4 × Ø8 × 2 mm rubber bumpers (recesses on the back).

## Assembly

1. Print the coupon and check the fit (above). Print the tray and the bezel.
2. Stick the four foam pads on the four standoff pads in the tray. (Insert option: fit the
   heat-set inserts in the four pilots now.)
3. Feed the speaker's two tinned leads from inside the cable bay out through the teardrop slot
   in the end wall. Leave ≈ 25 mm between the speaker and the saddle.
4. Cinch the lead pair to the saddle's crossbar with the zip tie (the tie passes under the bar),
   or tie a knot in the pair inside the bay. Pulling on the cable now loads the tie, not the
   solder joints.
5. Lower the speaker into the pocket, grille side up, with the leads lying in whichever of the two
   open-top slots in the pocket end wall matches your unit (the exit is beside one corner; both
   sides are slotted because it varies). Press it down onto the foam.
6. Place the bezel and drive the four screws diagonally. **Stop when the seam closes** - the rim
   is the stop; there is nothing to gain by tightening further.
7. Mount it: hang it on two wall screws through the keyholes (slots run toward the long side, so
   it hangs landscape; `keyhole_dir = "+x"` for portrait), screw it from behind a panel with
   M3 × 20, lash it with zip ties through the strap slots, or stick four bumpers in the foot
   recesses and stand it on a desk.

## Mounting features (all on the back, nothing shows from the front or sides)

- **Keyholes** × 2, 48 mm apart: Ø9 entry, 4.5 mm slot, for heads up to Ø8.5 × 2.8 mm (they sit
  in the 4 mm gap under the speaker).
- **Rear bores** Ø3.8 in line with the four bezel pilots: hidden M3 × 20 from behind a panel, or
  the stripped-pilot rescue above. Set `rear_bore_d = 0` if you want a plain back.
- **Strap slots** 5 × 2 mm, two pairs, 40 mm from centre: a zip tie or strap goes down one, across
  under the speaker and out the other.
- **Foot recesses** Ø8 × 0.6 for stick-on bumpers.

## Parameters worth knowing

Open the `.scad` in OpenSCAD and use the Customizer (Window → Customizer); every number is a
named parameter with a comment. The ones you are most likely to touch:

| Parameter | Default | Change it when |
|---|---|---|
| `clr_xy` | 0.6 | the coupon is tight (raise) or loose (lower) |
| `foam_t`, `z_preload` | 2.0, 1.0 | your speaker measures outside 20.3–21.5 mm thick: use 1 or 3 mm foam, or change the preload |
| `grille_style` | `hex` | `slots` (62 % open), `bars` (51 %), `open` (bare window for grille cloth) |
| `hole_d`, `hole_pitch` | 4.0, 5.2 | printer undersizes holes badly: try 4.5 / 6.0 |
| `grille_patch_r` | 22 | 0 fills the whole window with holes instead of a round patch per cone |
| `lead_end`, `lead_slots` | `+x`, `both` | the cable should leave the other end / you want a single slot |
| `cable_exit` | `end` | `back` for a wall-mounted unit, `none` with the amp bay |
| `fastener`, `screw_head` | `selftap`, `csk` | heat-set inserts / pan heads |
| `keyholes`, `strap_slots`, `feet_recess`, `rear_bore_d` | on | you want a plain back |
| `amp_bay` | off | you want a small amplifier board (MAX98357A, PAM8403…) in the box: adds 28 mm to the lead end, two more screws, corner pads and zip-tie slots for a `amp_board` sized board. **Unverified against a real board.** |
| `ear_bosses` | off | you have measured the ear plane and want M3 screws through the ears as well. **Experimental.** |

The model `echo()`s the outer size and the printed open area over each cone, and `assert()`s
the things that would make a print fail: minimum walls, grille webs, screw-head edge distance,
pilot-to-cavity walls, bridge lengths, bed size and ≥ 40 % open area.

## Re-rendering and checking

```sh
./render.sh            # stl/ + img/ (previews need a display; headless: xvfb-run -a ./render.sh)
./render.sh stl        # STLs only
```

Any OpenSCAD from 2021.01 renders it (bezel ≈ 3 min with CGAL); a 2024+ build with the
manifold backend does each part in about a second and is picked up automatically.

```sh
pip install trimesh manifold3d
python3 check_model.py --parts stl/speaker_enclosure_tray.stl stl/speaker_enclosure_bezel.stl stl/speaker_enclosure_coupon.stl
# assembly interference (parts rendered in place):
openscad -o /tmp/tray.stl  -D 'part="tray"'          speaker_enclosure.scad
openscad -o /tmp/bezel.stl -D 'part="bezel_inplace"' speaker_enclosure.scad
openscad -o /tmp/spk.stl   -D 'part="speaker"'       speaker_enclosure.scad
python3 check_model.py --assembly --parts /tmp/tray.stl /tmp/bezel.stl --speaker /tmp/spk.stl
```

## Design notes

- **Z stack** (from the bed): 2.0 floor · 3.0 standoff pads · 2.0 foam squashed to 1.0 · 21 speaker
  → front face and tray rim both at 27.0 · 4.0 bezel. Positive preload for speaker thickness
  20.3–21.5 mm. The 4 mm gap under the speaker is what hides the keyhole heads and straps.
- **Pocket** 101.2 × 46.2 (0.6 per side) with R1 corners and eight 0.3 mm locating ribs: six on the
  long walls at z 21.5–26 (on the full-width front shell for any parting line 6–9 mm behind the
  face), two on the end walls at z 7.5–12.5 (on the full-length rear shell). Effective play 0.3
  per side, locked by the clamp.
- **Bezel window** 86 × 40: the band bears 2.5 mm on each long side of the speaker's front face
  and on the 3 mm centre bar between the cones. The front shell ends about 9 mm short of the
  overall 100 mm (the ears and the rear shell fill the rest, 7–8 mm lower), so the band floats
  there; that is intended. The grille plate is 1.6 thick and stands 2.4 mm above the speaker face.
- **Grille**: Ø4.0 holes on a 5.2 mm hex pitch (1.2 mm webs), a round patch over each cone, 0.3 mm
  chamfer on the bed side. 42 % open over each cone at a 0.3 mm printed undersize (54 % nominal).
- **Cable**: two 12 mm open-top U-slots in the pocket end wall (floor 14 mm below the front face,
  well under the parting-line exit), an 8.4 mm cable bay, a goal-post saddle for a zip tie, and a
  3.6 × 8 mm teardrop exit (round bottom, 45° apex: no bridge). The teardrop passes the tinned
  ends one at a time and blocks a knot or a zip-tie head.
- **What is not verified on hardware**: nothing here has met a real speaker yet. The coupon exists
  for that reason. The numbers most likely to need a tweak are `clr_xy` and `foam_t`.

## Sources

- Core Electronics product page and forum thread for CE08483 (dimensions 100 × 45 × 21 mm,
  91.2 × 36.2 mm mounting pattern, Ø35 cones ≈ 27.5 mm from each end, 250 mm tinned leads).
- Waveshare "8Ω 5W Speaker" product photos (same moulding; the 92 × 36 mm dimensioned photo and
  the front/back views used to read the ear and shell geometry).
- Rare Components listing (confirms 100 × 45 × 21 mm).
