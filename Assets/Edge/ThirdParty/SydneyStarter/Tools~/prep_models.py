# Blender headless: converts the starter-pack portable models into clean Unity-ready FBX
# (transforms applied incl. mirrored nodes, origin at base centre, explicit _LOD0/1/2 meshes,
# textures written as separate files). Originals are only read.
import bpy, sys, os, json, mathutils
args = sys.argv[sys.argv.index('--')+1:]
src, out_dir, tex_dir, name = args[0], args[1], args[2], args[3]
lod_ratios = [float(x) for x in args[4].split(',')]
os.makedirs(out_dir, exist_ok=True); os.makedirs(tex_dir, exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)
if src.endswith('.glb'): bpy.ops.import_scene.gltf(filepath=src)
else: bpy.ops.import_scene.fbx(filepath=src)
meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
# Apply every parent transform (bakes the mirrored -1 scale nodes; Blender flips the winding).
for o in meshes:
    mw = o.matrix_world.copy()
    o.parent = None
    o.matrix_world = mw
bpy.ops.object.select_all(action='DESELECT')
for o in meshes: o.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
for o in list(bpy.context.scene.objects):
    if o.type != 'MESH': bpy.data.objects.remove(o, do_unlink=True)
if len(meshes) > 1: bpy.ops.object.join()
obj = bpy.context.view_layer.objects.active
obj.name = name + "_LOD0"; obj.data.name = name + "_LOD0"
# Make sure normals face out after the mirrored bakes.
bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.mesh.normals_make_consistent(inside=False); bpy.ops.object.mode_set(mode='OBJECT')
# Origin at the base: centre of the lowest 5% of vertices in XY, min Z.
vs = [obj.matrix_world @ v.co for v in obj.data.vertices]
zmin = min(v.z for v in vs); zmax = max(v.z for v in vs)
low = [v for v in vs if v.z <= zmin + 0.05*(zmax-zmin)]
cx = sum(v.x for v in low)/len(low); cy = sum(v.y for v in low)/len(low)
obj.data.transform(mathutils.Matrix.Translation((-cx, -cy, -zmin)))
obj.location = (0, 0, 0)
def tris(o): return sum(len(p.vertices)-2 for p in o.data.polygons)
info = {"name": name, "source": os.path.basename(src), "lods": []}
lods = [obj]
for i, r in enumerate(lod_ratios):
    if i == 0 and r < 1.0:
        m = obj.modifiers.new("dec", 'DECIMATE'); m.ratio = r
        bpy.ops.object.modifier_apply(modifier="dec")
        continue
    if i == 0: continue
    c = obj.copy(); c.data = obj.data.copy(); bpy.context.scene.collection.objects.link(c)
    c.name = c.data.name = f"{name}_LOD{i}"
    m = c.modifiers.new("dec", 'DECIMATE'); m.ratio = r / lod_ratios[0] if lod_ratios[0] < 1 else r
    bpy.context.view_layer.objects.active = c
    bpy.ops.object.modifier_apply(modifier="dec")
    lods.append(c)
for o in lods:
    d = o.dimensions
    info["lods"].append({"object": o.name, "triangles": tris(o), "dimensions_m_blender_xyz": [round(d.x,4), round(d.y,4), round(d.z,4)]})
# Textures -> separate files named <material>__<socket-ish>.<ext>
mats = {}
for mat in obj.data.materials:
    if not mat or not mat.use_nodes: continue
    for n in mat.node_tree.nodes:
        if n.type == 'TEX_IMAGE' and n.image:
            img = n.image
            base = os.path.splitext(img.name)[0].replace('.tga','').replace(' ','_')
            ext = '.png' if (img.file_format == 'PNG' or img.name.lower().endswith('.png')) else '.jpg'
            dst = os.path.join(tex_dir, base + ext)
            if not os.path.exists(dst):
                img2 = img.copy(); img2.filepath_raw = dst
                img2.file_format = 'PNG' if ext == '.png' else 'JPEG'
                try: img2.save()
                except Exception as e:
                    sc = bpy.context.scene; sc.render.image_settings.file_format = img2.file_format
                    img.save_render(dst)
            mats.setdefault(mat.name, []).append({"file": os.path.basename(dst), "links": [l.to_socket.name for l in n.outputs[0].links], "colorspace": img.colorspace_settings.name})
info["materials"] = mats
bpy.ops.object.select_all(action='DESELECT')
for o in lods: o.select_set(True)
fbx = os.path.join(out_dir, name + ".fbx")
bpy.ops.export_scene.fbx(filepath=fbx, use_selection=True, object_types={'MESH'}, axis_forward='-Z', axis_up='Y',
                         apply_scale_options='FBX_SCALE_NONE', mesh_smooth_type='FACE', path_mode='STRIP', embed_textures=False,
                         add_leaf_bones=False, bake_anim=False)
info["fbx"] = os.path.basename(fbx)
json.dump(info, open(os.path.join(out_dir, name + "_prep.json"), "w"), indent=2)
print("PREP", json.dumps(info))
