#!/usr/bin/env python3
"""Mesh checks for the speaker enclosure STLs.

Usage:
    # print-ready parts: watertight + bed-size checks only
    python3 check_model.py --parts stl/speaker_enclosure_tray.stl stl/speaker_enclosure_bezel.stl ...

    # assembly check: parts rendered IN PLACE (part="tray", "bezel_inplace") against the
    # speaker (part="envelope" or part="speaker"); every pair must have ~0 intersection
    python3 check_model.py --assembly --parts tray.stl bezel_inplace.stl --speaker envelope.stl

For every part it reports whether the mesh is watertight, its volume and bounding box;
with --assembly also the intersection volume with every other part and with the
speaker: anything but ~0 is an interference.

Requires: trimesh, manifold3d (pip install trimesh manifold3d).
Exit status 1 if any part is not watertight, exceeds the bed, or (with --assembly) any
pair interferes by more than `--tol` mm^3.
"""
import argparse
import sys

import numpy as np
import trimesh
from manifold3d import Manifold, Mesh


def to_manifold(tm: trimesh.Trimesh) -> Manifold:
    m = Mesh(vert_properties=np.asarray(tm.vertices, dtype=np.float32),
             tri_verts=np.asarray(tm.faces, dtype=np.uint32))
    return Manifold(m)


def load(path: str) -> trimesh.Trimesh:
    tm = trimesh.load(path, force="mesh")
    tm.merge_vertices()
    tm.update_faces(tm.nondegenerate_faces())
    tm.remove_unreferenced_vertices()
    return tm


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--parts", nargs="+", required=True)
    ap.add_argument("--assembly", action="store_true",
                    help="parts are in assembly coordinates: also test pairwise interference")
    ap.add_argument("--speaker", help="STL of the speaker envelope in assembly coordinates (implies --assembly)")
    ap.add_argument("--tol", type=float, default=0.5, help="allowed interference volume, mm^3")
    ap.add_argument("--bed", type=float, default=180, help="print bed side, mm")
    args = ap.parse_args()

    ok = True
    meshes = {}
    for p in args.parts:
        tm = load(p)
        meshes[p] = tm
        ext = tm.bounding_box.extents
        print(f"{p}: watertight={tm.is_watertight} volume={tm.volume:9.1f} mm3 "
              f"bbox={ext.round(2).tolist()} zmin={tm.bounds[0][2]:.2f} zmax={tm.bounds[1][2]:.2f}")
        if not tm.is_watertight:
            print("   !! not watertight")
            ok = False
        if max(ext[0], ext[1]) > args.bed:
            print(f"   !! footprint exceeds {args.bed} mm bed")
            ok = False

    mans = {p: to_manifold(m) for p, m in meshes.items()}
    names = list(mans)
    if args.assembly or args.speaker:
        for i, a in enumerate(names):
            for b in names[i + 1:]:
                v = (mans[a] ^ mans[b]).volume()
                flag = "" if v <= args.tol else "   !! INTERFERENCE"
                print(f"{a} ∩ {b}: {v:.2f} mm3{flag}")
                if v > args.tol:
                    ok = False

    if args.speaker:
        spk = to_manifold(load(args.speaker))
        print(f"speaker envelope volume {spk.volume():.1f} mm3")
        for a in names:
            v = (mans[a] ^ spk).volume()
            flag = "" if v <= args.tol else "   !! HOUSING CUTS INTO THE SPEAKER ENVELOPE"
            print(f"{a} ∩ speaker: {v:.2f} mm3{flag}")
            if v > args.tol:
                ok = False

    print("RESULT:", "PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
