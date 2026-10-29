# Copies the Poly Haven CC0 maps Unity needs and packs an HDRP mask (R metal 0, G AO, B 0, A smoothness = 1 - roughness).
import sys, os, shutil
from PIL import Image, ImageOps
src_root, dst_root = sys.argv[1], sys.argv[2]
for id in ["concrete_pavement_02", "gravel_road", "sandstone_cracks", "eucalyptus_bark"]:
    s = os.path.join(src_root, id); d = os.path.join(dst_root, id); os.makedirs(d, exist_ok=True)
    shutil.copy2(os.path.join(s, f"{id}_diff_2k.jpg"), d)
    shutil.copy2(os.path.join(s, f"{id}_nor_gl_2k.png"), d)
    ao = Image.open(os.path.join(s, f"{id}_ao_2k.png")).convert("L")
    rough = Image.open(os.path.join(s, f"{id}_rough_2k.png")).convert("L")
    zero = Image.new("L", ao.size, 0)
    Image.merge("RGBA", (zero, ao, zero, ImageOps.invert(rough))).save(os.path.join(d, f"{id}_hdrpmask_2k.png"))
    print("tex", id, "ok")
def mask_from(ao_path, rough_path, out):
    rough = Image.open(rough_path).convert("L")
    ao = Image.open(ao_path).convert("L").resize(rough.size) if ao_path and os.path.exists(ao_path) else Image.new("L", rough.size, 255)
    zero = Image.new("L", rough.size, 0)
    Image.merge("RGBA", (zero, ao, zero, ImageOps.invert(rough))).save(out)
if len(sys.argv) > 4:
    mask_from(sys.argv[3], sys.argv[4], sys.argv[5]); print("scan mask ok")
