import os
# OpenDock ad, hero shot: the Dock pops out of the laptop's screen in 3D. A slab of real glass (the Dock's pane, as
# deep as glass looks) lifts off the screen, bending the wallpaper behind it, and grows toward the camera; the icons
# (OpenDock's own Liquid Glass (colors) icons) pop up off it in a wave from the middle. Frames 1000-1180 of the
# "OpenDock Ad" scene (the lid is open by then), with their own camera. Run after scene_laptop.py (video/bl.py).
import bpy, bmesh, math, os
from mathutils import Vector, Matrix

ROOT = os.environ.get("OPENDOCK_VIDEO") or os.path.join(os.path.expanduser("~"), "Desktop", "mydockfinder-opensource", "video")
ICONS = os.path.join(ROOT, "assets", "icons")
F0, F1 = 1000, 1180
scene = bpy.data.scenes["OpenDock Ad"]
bpy.context.window.scene = scene

# start clean: last run's hero objects go
coll = bpy.data.collections.get("Hero") or bpy.data.collections.new("Hero")
if coll.name not in scene.collection.children: scene.collection.children.link(coll)
for o in list(coll.objects): bpy.data.objects.remove(o, do_unlink=True)
def link(o):
    coll.objects.link(o); return o

def anim_curves(idblock):
    ad = idblock.animation_data
    if not ad or not ad.action: return []
    try: return list(ad.action.fcurves)
    except AttributeError: return [fc for l in ad.action.layers for st in l.strips for bag in st.channelbags for fc in bag.fcurves]
def interp(idblock, kind="BEZIER", handles="AUTO_CLAMPED"):
    for fc in anim_curves(idblock):
        for k in fc.keyframe_points:
            k.interpolation = kind
            if kind == "BEZIER": k.handle_left_type = k.handle_right_type = handles

# ---------- the screen: the Dock leaves it (desk_nodock.png from frame F0 + 12) ----------
disp = scene.objects["Display"]
mat = disp.data.materials[0]; nt = mat.node_tree
bsdf = nt.nodes["Principled BSDF"]
mixc = nt.nodes["Screen Switch"]
old = nt.nodes.get("Hero Switch")
if old: nt.nodes.remove(old)
tex3 = nt.nodes.new("ShaderNodeTexImage")
tex3.image = bpy.data.images.load(os.path.join(ROOT, "assets", "desk_nodock.png"), check_existing=True); tex3.image.reload()
mix2 = nt.nodes.new("ShaderNodeMix"); mix2.data_type = "RGBA"; mix2.name = "Hero Switch"
nt.links.new(mixc.outputs["Result"], mix2.inputs["A"]); nt.links.new(tex3.outputs["Color"], mix2.inputs["B"])
nt.links.new(mix2.outputs["Result"], bsdf.inputs["Emission Color"])
f = mix2.inputs["Factor"]
f.default_value = 0; f.keyframe_insert("default_value", frame=F0 + 11)
f.default_value = 1; f.keyframe_insert("default_value", frame=F0 + 12)
sw = mixc.inputs["Factor"]                    # before the pop: the screen with the Dock (the first picture)
sw.default_value = 1; sw.keyframe_insert("default_value", frame=F0 - 1)
sw.default_value = 0; sw.keyframe_insert("default_value", frame=F0)
for fc in anim_curves(nt):
    for k in fc.keyframe_points: k.interpolation = "CONSTANT"

# ---------- where the Dock is on the screen, in the world ----------
scene.frame_set(F0); bpy.context.view_layer.update()
M = disp.matrix_world.copy()
sw_m, sh_m = disp.dimensions.x, disp.dimensions.y
rot = M.to_3x3().normalized()
right, up, normal = (rot @ Vector((1, 0, 0))).normalized(), (rot @ Vector((0, 1, 0))).normalized(), (rot @ Vector((0, 0, 1))).normalized()
main_cam = [o for o in scene.objects if o.type == "CAMERA" and not o.name.startswith("Hero")][0]
cam0 = main_cam.matrix_world.translation
if normal.dot(cam0 - M.translation) < 0: normal = -normal
if up.z < 0: up = -up                          # the image's top is the lid's top
right = up.cross(normal).normalized()
mpd = sw_m / 2560.0                            # metres per screen DIP
def screen_point(px, py):                      # screen DIPs (0..2560, 0..1440 from the top) -> world
    return M.translation + right * ((px - 1280) * mpd) + up * ((720 - py) * mpd)
lines = [l.split() for l in open(os.path.join(ICONS, "icons.txt")) if not l.startswith("#")]
head = open(os.path.join(ICONS, "icons.txt")).readline().split("pane:")[1].strip(" )\n").split()
paneL, paneT, paneW, paneH = [float(v) for v in head]
centre = screen_point(paneL + paneW / 2, paneT + paneH / 2)
frame = Matrix((right, up, normal)).transposed().to_4x4(); frame.translation = centre

dock = link(bpy.data.objects.new("Hero Dock", None)); dock.empty_display_size = 0.02
dock.matrix_world = frame
base_loc = dock.location.copy(); base_rot = dock.rotation_euler.copy()

# ---------- the glass: the Dock's pane as a slab ----------
W_, H_, R_, T_ = paneW * mpd, paneH * mpd, paneH * 0.29 * mpd, 0.0034
bm = bmesh.new()
pts = []
for (cx, cy, a0) in ((W_ / 2 - R_, H_ / 2 - R_, 0), (-W_ / 2 + R_, H_ / 2 - R_, 90), (-W_ / 2 + R_, -H_ / 2 + R_, 180), (W_ / 2 - R_, -H_ / 2 + R_, 270)):
    for i in range(9):
        a = math.radians(a0 + i * 90 / 8)
        pts.append((cx + R_ * math.cos(a), cy + R_ * math.sin(a)))
bottom = [bm.verts.new((x, y, 0)) for x, y in pts]
face = bm.faces.new(bottom)
ext = bmesh.ops.extrude_face_region(bm, geom=[face])
for v in [e for e in ext["geom"] if isinstance(e, bmesh.types.BMVert)]: v.co.z += T_
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
me = bpy.data.meshes.new("Hero Glass"); bm.to_mesh(me); bm.free()
glass = link(bpy.data.objects.new("Hero Glass", me)); glass.parent = dock
bv = glass.modifiers.new("Bevel", "BEVEL"); bv.width = T_ * 0.42; bv.segments = 6; bv.limit_method = "ANGLE"
for p in glass.data.polygons: p.use_smooth = True
gm = bpy.data.materials.get("Hero Glass") or bpy.data.materials.new("Hero Glass")
gm.use_nodes = True
gb = gm.node_tree.nodes["Principled BSDF"]
gb.inputs["Base Color"].default_value = (0.96, 0.97, 1.0, 1)
gb.inputs["Transmission Weight"].default_value = 1.0
gb.inputs["Roughness"].default_value = 0.06
gb.inputs["IOR"].default_value = 1.45
glass.data.materials.append(gm)

# ---------- the icons, each a picture standing just above the glass ----------
def icon_material(path):
    name = "Hero " + os.path.basename(path)
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    try: m.surface_render_method = "BLENDED"
    except Exception: pass
    t = m.node_tree
    for n in list(t.nodes): t.nodes.remove(n)
    out = t.nodes.new("ShaderNodeOutputMaterial"); tex = t.nodes.new("ShaderNodeTexImage")
    tex.image = bpy.data.images.load(path, check_existing=True); tex.image.reload()
    # the icon's own colours (emission, so they read exactly), on a surface solid enough to cast a soft shadow
    pb = t.nodes.new("ShaderNodeBsdfPrincipled")
    pb.inputs["Base Color"].default_value = (0, 0, 0, 1); pb.inputs["Roughness"].default_value = 0.35
    pb.inputs["Emission Strength"].default_value = 1.0
    t.links.new(tex.outputs["Color"], pb.inputs["Emission Color"]); t.links.new(tex.outputs["Alpha"], pb.inputs["Alpha"])
    t.links.new(pb.outputs[0], out.inputs["Surface"])
    return m
slot = 79 * mpd
tile = slot * 224 / 256                        # IconLook's tile inside the 256 px icon
tile_d = tile * 0.18                           # its thickness
bm = bmesh.new()
n = 5.0; ring = []
for k in range(96):                            # Apple's icon shape: |x|^5 + |y|^5 = r^5 (IconLook.Squircle)
    t = k * 2 * math.pi / 96; ct, st = math.cos(t), math.sin(t)
    ring.append(bm.verts.new((tile / 2 * math.copysign(abs(ct) ** (2 / n), ct), tile / 2 * math.copysign(abs(st) ** (2 / n), st), 0)))
f0 = bm.faces.new(ring)
ex = bmesh.ops.extrude_face_region(bm, geom=[f0])
for v in [e for e in ex["geom"] if isinstance(e, bmesh.types.BMVert)]: v.co.z += tile_d
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
tile_mesh = bpy.data.meshes.get("Hero Tile") or bpy.data.meshes.new("Hero Tile")
bm.to_mesh(tile_mesh); bm.free()
for p_ in tile_mesh.polygons: p_.use_smooth = True
tile_glass = bpy.data.materials.get("Hero Tile Glass") or bpy.data.materials.new("Hero Tile Glass")
tile_glass.use_nodes = True
tg = tile_glass.node_tree.nodes["Principled BSDF"]
tg.inputs["Base Color"].default_value = (1, 1, 1, 1); tg.inputs["Transmission Weight"].default_value = 0.92
tg.inputs["Roughness"].default_value = 0.12; tg.inputs["IOR"].default_value = 1.45; tg.inputs["Coat Weight"].default_value = 0.6
if not tile_mesh.materials: tile_mesh.materials.append(tile_glass)
icons = []
xs = [(float(cx) - 1280) * mpd for (i, cx, sep) in lines]
span = max(abs(x) for x in xs)
for (i, cx, sep) in lines:
    x = (float(cx) - 1280) * mpd
    if sep == "1":
        bpy.ops.mesh.primitive_plane_add(size=1); o = bpy.context.active_object
        for c in o.users_collection: c.objects.unlink(o)
        coll.objects.link(o); o.name = "Hero Separator"; o.parent = dock
        o.scale = (0.12 * mpd * 8, slot * 0.62, 1); o.location = (x, 0, T_ + 0.0002)
        sm = bpy.data.materials.get("Hero Separator") or bpy.data.materials.new("Hero Separator"); sm.use_nodes = True
        sm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (1, 1, 1, 1)
        sm.node_tree.nodes["Principled BSDF"].inputs["Emission Color"].default_value = (1, 1, 1, 1)
        sm.node_tree.nodes["Principled BSDF"].inputs["Emission Strength"].default_value = 0.6
        o.data.materials.append(sm)
        continue
    # a real 3D tile: a block of glass in the icon's squircle with rounded edges, its artwork on the front face
    o = link(bpy.data.objects.new("Hero Icon %s" % i, None)); o.parent = dock; o.empty_display_size = slot / 2
    body = link(bpy.data.objects.new("Hero Icon %s Glass" % i, tile_mesh)); body.parent = o
    bv = body.modifiers.new("Bevel", "BEVEL"); bv.width = tile_d * 0.38; bv.segments = 5; bv.limit_method = "ANGLE"
    bpy.ops.mesh.primitive_plane_add(size=1); face = bpy.context.active_object
    for c in face.users_collection: c.objects.unlink(face)
    coll.objects.link(face); face.name = "Hero Icon %s Face" % i; face.parent = o
    face.scale = (tile, tile, 1); face.location = (0, 0, tile_d + 0.00006)
    uv = face.data.uv_layers.active.data
    for li, loop in enumerate(face.data.loops):            # just the tile from the 256 px picture (no shadow margin)
        u, v = uv[li].uv
        uv[li].uv = (16 / 256 + u * 224 / 256, 16 / 256 + v * 224 / 256)
    face.data.materials.append(icon_material(os.path.join(ICONS, "icon_%02d.png" % int(i))))
    icons.append((o, x))

# ---------- the motion ----------
lift = 0.12         # how far out of the screen it comes (m)
grow = 2.5
def key_dock(frame, out, scale, tilt=0.0, turn=0.0):
    dock.location = base_loc + normal * out + up * (out * 0.25)
    dock.rotation_euler = base_rot; dock.rotation_euler.rotate_axis("X", math.radians(tilt)); dock.rotation_euler.rotate_axis("Y", math.radians(turn))
    dock.scale = (scale, scale, scale)
    for p in ("location", "rotation_euler", "scale"): dock.keyframe_insert(p, frame=frame)
key_dock(F0, 0.0006, 1.0)
key_dock(F0 + 12, 0.0006, 1.0)
key_dock(F0 + 40, lift * 1.06, grow * 1.04, -8, 10)  # out it comes and turns to the camera, a touch past...
key_dock(F0 + 54, lift, grow, -6, 8)               # ...and settles
key_dock(F1, lift * 1.02, grow * 1.02, -5, 12)      # a slow turn while it hovers
interp(dock)
for (o, x) in icons:
    d = abs(x) / span                               # from the middle outward
    s0 = F0 + 22 + d * 14
    z_rest, z_up = T_ + 0.0004, T_ + 0.0095     # well clear of the glass: they float
    o.location = (x, 0, z_rest); o.scale = (1, 1, 1)
    o.keyframe_insert("location", frame=s0); o.keyframe_insert("scale", frame=s0)
    o.location = (x, 0.0006, z_up * 1.25); o.scale = (1.14, 1.14, 1.14)
    o.keyframe_insert("location", frame=s0 + 8); o.keyframe_insert("scale", frame=s0 + 8)
    o.location = (x, 0.0004, z_up); o.scale = (1.08, 1.08, 1.08)
    o.keyframe_insert("location", frame=s0 + 16); o.keyframe_insert("scale", frame=s0 + 16)
    # a gentle bob while it hovers, each icon a little out of step
    for j, fr in enumerate(range(int(s0 + 30), F1 + 1, 30)):
        o.location = (x, 0.0004, z_up + 0.0014 * math.sin(j * 1.7 + x * 90)); o.keyframe_insert("location", frame=fr)
    interp(o)

# ---------- the camera: face-on, then arcing round as the Dock comes out ----------
cam_d = bpy.data.cameras.get("Hero Camera") or bpy.data.cameras.new("Hero Camera")
cam_d.lens = 35; cam_d.dof.use_dof = True; cam_d.dof.aperture_fstop = 5.6
cam = link(bpy.data.objects.new("Hero Camera", cam_d))
aim = link(bpy.data.objects.new("Hero Aim", None))
tr = cam.constraints.new("TRACK_TO"); tr.target = aim; tr.track_axis = "TRACK_NEGATIVE_Z"; tr.up_axis = "UP_Y"
cam_d.dof.focus_object = aim
scr_mid = screen_point(1280, 900)
dock_out = centre + normal * lift + up * (lift * 0.25)
def key_cam(frame, pos, look):
    cam.location = pos; aim.location = look
    cam.keyframe_insert("location", frame=frame); aim.keyframe_insert("location", frame=frame)
key_cam(F0, scr_mid + normal * 0.62, scr_mid)
key_cam(F0 + 12, scr_mid + normal * 0.60, scr_mid + (centre - scr_mid) * 0.15)
key_cam(F0 + 54, dock_out + normal * 0.29 - right * 0.20 - up * 0.03, dock_out - right * 0.012)   # low and close, along the Dock
key_cam(F1, dock_out + normal * 0.30 - right * 0.16 - up * 0.022, dock_out - right * 0.004)
interp(cam); interp(aim)
# the hero camera takes over from F0
for m in [m for m in scene.timeline_markers if m.name == "Hero"]: scene.timeline_markers.remove(m)
mk = scene.timeline_markers.new("Hero", frame=F0); mk.camera = cam
first = [m for m in scene.timeline_markers if m.name == "Main"]
if not first:
    m0 = scene.timeline_markers.new("Main", frame=1); m0.camera = main_cam
for o in coll.objects: o.hide_render = False

scene.frame_end = max(scene.frame_end, F1)
scene.frame_set(1)
bpy.ops.wm.save_mainfile()
print("hero built:", len(coll.objects), "objects")
