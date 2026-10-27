#!/usr/bin/env bash
# Render the print-ready STLs (and preview PNGs) from speaker_enclosure.scad.
#
#   ./render.sh            # stl/ for tray, bezel, coupon + img/ previews
#   ./render.sh stl        # STLs only
#   OPENSCAD=/path/to/openscad ./render.sh
#
# Any OpenSCAD from 2021.01 works. A 2024+ build with the manifold backend renders
# the grille in seconds instead of minutes and is picked up automatically.
# PNG previews need a display; on a headless box run under `xvfb-run -a ./render.sh`.
set -euo pipefail
cd "$(dirname "$0")"
OPENSCAD="${OPENSCAD:-openscad}"
export QT_QPA_PLATFORM="${QT_QPA_PLATFORM:-offscreen}"

BACKEND=()
if "$OPENSCAD" --help 2>&1 | grep -q -- '--backend'; then BACKEND=(--backend=manifold); fi

mkdir -p stl img
for p in tray bezel coupon; do
    echo "== $p"
    "$OPENSCAD" "${BACKEND[@]}" -o "stl/speaker_enclosure_$p.stl" -D "part=\"$p\"" speaker_enclosure.scad 2>&1 \
        | grep -E 'ECHO|WARNING|ERROR|assert' || true
done

[[ "${1:-}" == "stl" ]] && exit 0

png() {  # name part camera(tx,ty,tz,rx,ry,rz,dist)
    "$OPENSCAD" -o "img/$1.png" --imgsize=1600,1000 --colorscheme=Tomorrow --projection=p \
        --camera="$3" -D "part=\"$2\"" ${4:-} speaker_enclosure.scad 2>/dev/null || true
}
png assembly        assembly   "0,0,14,60,0,35,330"
png assembly_back   assembly   "0,0,14,120,0,215,330"
png exploded        assembly   "0,0,25,60,0,35,380" "-D explode=30"
png section         section    "0,0,14,60,0,35,330"
png tray            tray       "0,0,14,55,0,35,330"
png bezel           bezel      "0,0,2,50,0,35,330"
png printplate      printplate "0,60,10,45,0,25,480"
echo "done: stl/ and img/"
