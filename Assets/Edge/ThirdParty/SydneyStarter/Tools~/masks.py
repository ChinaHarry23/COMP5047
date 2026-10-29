import sys, os, numpy as np
from PIL import Image
def load01(p, size=None):
    im = Image.open(p)
    a = np.asarray(im).astype(np.float64)
    if a.ndim == 3: a = a[..., 0]
    mx = 65535.0 if a.max() > 255 else 255.0
    a = a / mx
    if size and (a.shape[1], a.shape[0]) != size:
        a = np.asarray(Image.fromarray((a*255).astype(np.uint8)).resize(size, Image.BILINEAR)).astype(np.float64)/255.0
    return a, im.mode
def pack(ao_p, rough_p, out):
    r, mode = load01(rough_p)
    ao = load01(ao_p, (r.shape[1], r.shape[0]))[0] if ao_p else np.ones_like(r)
    z = np.zeros_like(r)
    rgba = np.stack([z, ao, z, 1.0 - r], -1)
    Image.fromarray((rgba*255+0.5).clip(0,255).astype(np.uint8), "RGBA").save(out)
    print(os.path.basename(out), "rough mode", mode, "AO mean %.2f smooth mean %.2f" % (ao.mean(), (1-r).mean()))
src, dst = sys.argv[1], sys.argv[2]
for id in ["concrete_pavement_02", "gravel_road", "sandstone_cracks", "eucalyptus_bark"]:
    pack(f"{src}/{id}/{id}_ao_2k.png", f"{src}/{id}/{id}_rough_2k.png", f"{dst}/Textures/{id}/{id}_hdrpmask_2k.png")
t = f"{src}/native_models/eucalyptus-tree-scan/original/textures"
pack(f"{t}/EucTree_AO.tga.png", f"{t}/EucTree_R.tga.png", f"{dst}/Models/eucalyptus_tree_scan/textures/EucTree_hdrpmask.png")
