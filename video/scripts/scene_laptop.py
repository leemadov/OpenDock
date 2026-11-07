import os
# OpenDock ad, shots 1-2: a white studio and an unbranded laptop whose lid opens to OpenDock.
# Runs inside Blender (sent by video/bl.py). Builds everything in its own scene, "OpenDock Ad".
import bpy, bmesh, math, os
from mathutils import Vector

ROOT = os.environ.get("OPENDOCK_VIDEO") or os.path.join(os.path.expanduser("~"), "Desktop", "mydockfinder-opensource", "video")
SCREEN = os.path.join(ROOT, "assets", "screen_desktop.png")

# ---------- a scene of our own ----------
scene = bpy.data.scenes.get("OpenDock Ad")
if scene is None:
    scene = bpy.data.scenes.new("OpenDock Ad")
else:
    for o in list(scene.objects):
        bpy.data.objects.remove(o, do_unlink=True)
bpy.context.window.scene = scene
scene.render.resolution_x, scene.render.resolution_y = 1920, 1080
scene.render.fps = 30
scene.frame_start, scene.frame_end = 1, 180
scene.render.engine = "BLENDER_EEVEE"
scene.view_settings.view_transform = "Standard"
scene.view_settings.look = "None"
scene.view_settings.exposure = -0.55
try:
    scene.eevee.use_raytracing = True
    scene.eevee.use_shadows = True
except Exception:
    pass

def link(o):
    scene.collection.objects.link(o)
    return o

def mat(name, color, metallic=0.0, rough=0.5, coat=0.0):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (*color, 1)
    b.inputs["Metallic"].default_value = metallic
    b.inputs["Roughness"].default_value = rough
    if "Coat Weight" in b.inputs: b.inputs["Coat Weight"].default_value = coat
    return m

def box(name, size, loc, bevel, material, segments=6, parent=None):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co = Vector((v.co.x * size[0], v.co.y * size[1], v.co.z * size[2]))
    bm.to_mesh(me); bm.free()
    o = link(bpy.data.objects.new(name, me))
    o.location = loc
    if bevel > 0:
        bv = o.modifiers.new("Bevel", "BEVEL"); bv.width = bevel; bv.segments = segments; bv.limit_method = "ANGLE"
    o.data.materials.append(material)
    for p in o.data.polygons: p.use_smooth = True
    if parent: o.parent = parent
    return o

# ---------- materials ----------
alu = mat("Laptop Graphite", (0.33, 0.34, 0.37), metallic=1.0, rough=0.3)
keycap = mat("Laptop Keys", (0.008, 0.008, 0.009), rough=0.85)
keycap.node_tree.nodes["Principled BSDF"].inputs["Specular IOR Level"].default_value = 0.12
well = mat("Laptop Well", (0.03, 0.031, 0.034), metallic=0.6, rough=0.45)
pad = mat("Laptop Trackpad", (0.26, 0.27, 0.3), metallic=0.85, rough=0.18)
bezel = mat("Laptop Bezel", (0.004, 0.004, 0.005), rough=0.3, coat=0.2)
white = mat("Studio White", (0.92, 0.92, 0.92), rough=0.9)

screen_m = bpy.data.materials.get("Laptop Screen") or bpy.data.materials.new("Laptop Screen")
screen_m.use_nodes = True
nt = screen_m.node_tree
for n in list(nt.nodes): nt.nodes.remove(n)
out = nt.nodes.new("ShaderNodeOutputMaterial")
bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
tex = nt.nodes.new("ShaderNodeTexImage")
img = bpy.data.images.load(SCREEN, check_existing=True)
img.reload()   # pick up a re-rendered screen
tex.image = img
bsdf.inputs["Base Color"].default_value = (0, 0, 0, 1)
bsdf.inputs["Roughness"].default_value = 0.35
bsdf.inputs["Specular IOR Level"].default_value = 0.15
bsdf.inputs["Coat Weight"].default_value = 0.12
# shot 9 shows the screen as shot 8 leaves it: a second picture, switched in at frame 241
tex2 = nt.nodes.new("ShaderNodeTexImage")
img2 = bpy.data.images.load(os.path.join(ROOT, "assets", "shot8_end.png"), check_existing=True); img2.reload()
tex2.image = img2
mixc = nt.nodes.new("ShaderNodeMix"); mixc.data_type = "RGBA"; mixc.name = "Screen Switch"
mixc.inputs["Factor"].default_value = 0.0
nt.links.new(tex.outputs["Color"], mixc.inputs["A"]); nt.links.new(tex2.outputs["Color"], mixc.inputs["B"])
nt.links.new(mixc.outputs["Result"], bsdf.inputs["Emission Color"])
sw_in = mixc.inputs["Factor"]
sw_in.default_value = 0.0; sw_in.keyframe_insert("default_value", frame=240)
sw_in.default_value = 1.0; sw_in.keyframe_insert("default_value", frame=241)
for fc in (nt.animation_data.action.fcurves if hasattr(nt.animation_data.action, "fcurves") else []):
    for kp in fc.keyframe_points: kp.interpolation = "CONSTANT"
bsdf.inputs["Emission Strength"].default_value = 0.0   # animated: the screen wakes as the lid opens
nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])

# ---------- the white studio: a seamless curved backdrop ----------
prof = []
for i in range(0, 8): prof.append((-6 + i * 1.0, 0.0))                     # floor, front to back (to y = 1)
R = 2.2
for i in range(1, 17):
    a = i / 16 * math.pi / 2
    prof.append((1.0 + R * math.sin(a), R - R * math.cos(a)))               # the curve, well behind the laptop
prof.append((1.0 + R, 6.0))                                                 # the wall
me = bpy.data.meshes.new("Cyclorama")
bm = bmesh.new()
cols = []
for x in (-8.0, 8.0):
    cols.append([bm.verts.new((x, y, z)) for (y, z) in prof])
for i in range(len(prof) - 1):
    bm.faces.new((cols[0][i], cols[1][i], cols[1][i + 1], cols[0][i + 1]))
bm.to_mesh(me); bm.free()
cyc = link(bpy.data.objects.new("Cyclorama", me))
for p in cyc.data.polygons: p.use_smooth = True
cyc.data.materials.append(white)

world = bpy.data.worlds.get("Studio") or bpy.data.worlds.new("Studio")
world.use_nodes = True
bg = world.node_tree.nodes["Background"]
bg.inputs["Color"].default_value = (1, 1, 1, 1)
bg.inputs["Strength"].default_value = 0.28
scene.world = world

def area(name, loc, rot, size, power):
    l = bpy.data.lights.new(name, "AREA"); l.shape = "RECTANGLE"; l.size, l.size_y = size; l.energy = power
    o = link(bpy.data.objects.new(name, l)); o.location = loc; o.rotation_euler = [math.radians(a) for a in rot]
    return o
area("Key", (-0.9, -1.2, 1.6), (40, 0, -35), (1.6, 1.2), 27)
area("Fill", (1.4, -0.6, 0.9), (60, 0, 65), (1.4, 1.0), 8)
area("Top", (0, 0.2, 2.2), (0, 0, 0), (2.0, 2.0), 18)
area("Backdrop", (0, 0.9, 1.3), (30, 0, 0), (4.0, 1.5), 36)   # evens out the backdrop behind the laptop

# ---------- the laptop (metres; about a 14-inch machine) ----------
W, D, T = 0.312, 0.208, 0.0145          # base
LT = 0.0055                             # lid thickness
rig = link(bpy.data.objects.new("Laptop", None)); rig.empty_display_size = 0.1
base = box("Base", (W, D, T), (0, 0, T / 2), 0.0055, alu, parent=rig)
# keyboard well, keys, trackpad (on the top face)
import json
from mathutils import Matrix
KB = json.load(open(os.path.join(ROOT, "assets", "keyboard.json")))
P, GAP = 0.0185, 0.0028                      # key pitch and the gap between caps
KW, KH, KY = KB["width"] * P, KB["height"] * P, 0.034
box("Keyboard Well", (KW + 0.006, KH + 0.006, 0.0016), (0, KY, T - 0.0004), 0.003, well, parent=rig)
me = bpy.data.meshes.new("Keys")
bm = bmesh.new()
for k in KB["keys"]:
    cx = -KW / 2 + (k["x"] + k["w"] / 2) * P
    cy = KY + KH / 2 - (k["y"] + k["h"] / 2) * P
    m = Matrix.Translation((cx, cy, 0)) @ Matrix.Diagonal((k["w"] * P - GAP, k["h"] * P - GAP, 0.0018, 1))
    bmesh.ops.create_cube(bm, size=1.0, matrix=m)
bm.to_mesh(me); bm.free()
keys = link(bpy.data.objects.new("Keys", me)); keys.parent = rig; keys.location = (0, 0, T + 0.0003)
bv = keys.modifiers.new("Bevel", "BEVEL"); bv.width = 0.0016; bv.segments = 3; bv.limit_method = "ANGLE"
keys.data.materials.append(keycap)
for p_ in keys.data.polygons: p_.use_smooth = True
# the legends: one transparent layer over the key tops, drawn from the same layout (keyboard.py)
leg_m = bpy.data.materials.get("Key Legends") or bpy.data.materials.new("Key Legends")
leg_m.use_nodes = True
lnt = leg_m.node_tree
for n in list(lnt.nodes): lnt.nodes.remove(n)
lo = lnt.nodes.new("ShaderNodeOutputMaterial"); lb = lnt.nodes.new("ShaderNodeBsdfPrincipled"); lt = lnt.nodes.new("ShaderNodeTexImage")
lt.image = bpy.data.images.load(os.path.join(ROOT, "assets", "keyboard_legends.png"), check_existing=True)
lt.image.reload()
lnt.links.new(lt.outputs["Color"], lb.inputs["Base Color"]); lnt.links.new(lt.outputs["Alpha"], lb.inputs["Alpha"])
lb.inputs["Roughness"].default_value = 0.6
lnt.links.new(lb.outputs["BSDF"], lo.inputs["Surface"])
me = bpy.data.meshes.new("Legends")
bm = bmesh.new()
v = [bm.verts.new((x, y, 0)) for (x, y) in ((-KW / 2, KY - KH / 2), (KW / 2, KY - KH / 2), (KW / 2, KY + KH / 2), (-KW / 2, KY + KH / 2))]
f = bm.faces.new(v)
uvl = bm.loops.layers.uv.new()
for loop, c in zip(f.loops, ((0, 0), (1, 0), (1, 1), (0, 1))): loop[uvl].uv = c
bm.to_mesh(me); bm.free()
leg = link(bpy.data.objects.new("Legends", me)); leg.parent = rig; leg.location = (0, 0, T + 0.00125)
leg.data.materials.append(leg_m)
box("Trackpad", (0.118, 0.066, 0.0008), (0, -0.064, T - 0.0001), 0.004, pad, parent=rig)
# the hinge, at the back of the base; the lid hangs off it
hinge = link(bpy.data.objects.new("Hinge", None)); hinge.empty_display_size = 0.04
hinge.parent = rig; hinge.location = (0, D / 2 - 0.004, T + LT / 2)
# a real hinge: a barrel that turns with the lid, between two knuckles fixed to the base
hinge_m = mat("Laptop Hinge", (0.2, 0.205, 0.22), metallic=1.0, rough=0.38)
def barrel(name, length, x, parent, loc_y, loc_z, r=0.0043):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, segments=40, radius1=r, radius2=r, depth=length,
                          matrix=Matrix.Translation((x, 0, 0)) @ Matrix.Rotation(math.radians(90), 4, "Y"))
    bm.to_mesh(me); bm.free()
    o = link(bpy.data.objects.new(name, me)); o.parent = parent; o.location = (0, loc_y, loc_z)
    bv = o.modifiers.new("Bevel", "BEVEL"); bv.width = 0.0008; bv.segments = 2; bv.limit_method = "ANGLE"
    for p_ in o.data.polygons: p_.use_smooth = True
    o.data.materials.append(hinge_m)
    return o
barrel("Hinge Barrel", 0.206, 0.0, hinge, 0, 0)
for x in (-0.13, 0.13):
    barrel("Hinge Knuckle", 0.048, x, rig, D / 2 - 0.004, T + LT / 2, r=0.0046)
lid = box("Lid", (W, D - 0.004, LT), (0, -(D - 0.004) / 2, 0), 0.0045, alu, parent=hinge)
# the screen side faces down when closed: a glossy black bezel with the display inside it
bz = box("Bezel", (W - 0.006, D - 0.012, 0.0006), (0, -(D - 0.004) / 2, -LT / 2 - 0.0001), 0.004, bezel, segments=3, parent=hinge)
sw, sh = 0.300, 0.300 * 9 / 16          # a 16:9 display, like most Windows laptops (and the video)
me = bpy.data.meshes.new("Display")
bm = bmesh.new()
v = [bm.verts.new((x, y, 0)) for (x, y) in ((-sw / 2, -sh / 2), (sw / 2, -sh / 2), (sw / 2, sh / 2), (-sw / 2, sh / 2))]
f = bm.faces.new(v)
uv = bm.loops.layers.uv.new()
for loop, c in zip(f.loops, ((0, 0), (1, 0), (1, 1), (0, 1))): loop[uv].uv = c
bm.to_mesh(me); bm.free()
disp = link(bpy.data.objects.new("Display", me)); disp.data.materials.append(screen_m)
disp.parent = hinge
# facing down (-Z) when closed, image upright when the lid is open (top of the image at the hinge)
disp.rotation_euler = (math.radians(180), 0, 0)
disp.location = (0, -(D - 0.004) + 0.010 + sh / 2, -LT / 2 - 0.0008)   # thin bezel at the top, a chin by the hinge
# soft feet so the base doesn't float
for x in (-0.12, 0.12):
    for y in (-0.085, 0.085):
        box("Foot", (0.018, 0.018, 0.0015), (x, y, -0.0006), 0.004, keycap, segments=2, parent=rig)

# ---------- animation: the lid opens and the screen wakes (shots 1-2, frames 1-180) ----------
hinge.rotation_euler = (0, 0, 0)
hinge.keyframe_insert("rotation_euler", index=0, frame=40)
hinge.rotation_euler = (math.radians(-108), 0, 0)
hinge.keyframe_insert("rotation_euler", index=0, frame=110)
def ease(obj):
    ad = obj.animation_data
    if not ad or not ad.action: return
    try:
        curves = ad.action.fcurves
    except AttributeError:   # Blender 5 layered actions
        curves = [fc for layer in ad.action.layers for strip in layer.strips for bag in strip.channelbags for fc in bag.fcurves]
    for fc in curves:
        for k in fc.keyframe_points:
            k.interpolation = "BEZIER"; k.easing = "AUTO"
            k.handle_left_type = k.handle_right_type = "AUTO_CLAMPED"
ease(hinge)
em = bsdf.inputs["Emission Strength"]
em.default_value = 0.0; em.keyframe_insert("default_value", frame=70)
em.default_value = 1.45; em.keyframe_insert("default_value", frame=105)

# ---------- camera: three-quarter view, a little above, slowly drifting in ----------
cam_d = bpy.data.cameras.new("Camera"); cam_d.lens = 55
cam_d.dof.use_dof = True; cam_d.dof.aperture_fstop = 11.0
cam = link(bpy.data.objects.new("Camera", cam_d))
target = link(bpy.data.objects.new("Look At", None))
target.location = (0, 0.0, 0.012); target.keyframe_insert("location", frame=1); target.keyframe_insert("location", frame=35)
target.location = (0, 0.035, 0.085); target.keyframe_insert("location", frame=120)
tr = cam.constraints.new("TRACK_TO"); tr.target = target; tr.track_axis = "TRACK_NEGATIVE_Z"; tr.up_axis = "UP_Y"
cam_d.dof.focus_object = target
cam.location = (-0.49, -0.66, 0.30); cam.keyframe_insert("location", frame=1)       # close on the closed laptop
cam.location = (-0.52, -0.69, 0.32); cam.keyframe_insert("location", frame=35)
cam.location = (-0.68, -0.94, 0.47); cam.keyframe_insert("location", frame=120)     # rises and widens as it opens
cam.location = (-0.61, -0.85, 0.41); cam.keyframe_insert("location", frame=180)     # and settles in
target.keyframe_insert("location", frame=180)

# ---------- shot 3: into the screen (frames 181-240) ----------
# The camera swings round to face the display, then pushes in until it fills the frame (bezel gone): the last
# frame is the screen itself, so the shots inside OpenDock can cut straight in.
scene.frame_end = 420
scene.frame_set(150)
bpy.context.view_layer.update()
dm = disp.matrix_world.copy()
centre = dm.translation.copy()
normal = (dm.to_3x3() @ Vector((0, 0, 1))).normalized()
if normal.dot(Vector((-0.61, -0.85, 0.41)) - centre) < 0: normal = -normal     # the side facing the viewer
fill = cam_d.lens / 36.0 * sw * 0.997          # distance at which the display's width just fills the frame
target.location = centre; target.keyframe_insert("location", frame=212)
cam.location = centre + normal * 0.78; cam.keyframe_insert("location", frame=212)   # face-on, the whole laptop
cam.location = centre + normal * fill; cam.keyframe_insert("location", frame=240)   # through the bezel
target.keyframe_insert("location", frame=240)
ease(cam); ease(target)
def smooth(obj, frame):
    # no stop at the hand-over from swing to push: one continuous move
    ad = obj.animation_data
    try:
        curves = ad.action.fcurves
    except AttributeError:
        curves = [fc for layer in ad.action.layers for strip in layer.strips for bag in strip.channelbags for fc in bag.fcurves]
    for fc in curves:
        for k in fc.keyframe_points:
            if abs(k.co.x - frame) < 0.5: k.handle_left_type = k.handle_right_type = "AUTO"
smooth(cam, 212); smooth(target, 212)

# ---------- shot 9: back out to the laptop, and the end card (frames 241-420) ----------
# From the screen filling the frame (as shot 3 ends), back through the bezel to face-on, then round to a three-quarter
# view with the laptop on the left: the right of the frame is left white for the wordmark and the link.
cam.location = centre + normal * fill; cam.keyframe_insert("location", frame=241)
target.location = centre; target.keyframe_insert("location", frame=241)
cam.location = centre + normal * 0.80; cam.keyframe_insert("location", frame=282)
target.location = centre; target.keyframe_insert("location", frame=282)
cam.location = (-0.66, -1.40, 0.54); cam.keyframe_insert("location", frame=350)
target.location = (0.27, 0.02, 0.07); target.keyframe_insert("location", frame=350)
cam.location = (-0.62, -1.33, 0.51); cam.keyframe_insert("location", frame=420)     # a slow drift in while the card holds
target.location = (0.27, 0.02, 0.07); target.keyframe_insert("location", frame=420)
ease(cam); ease(target)
smooth(cam, 212); smooth(target, 212); smooth(cam, 282); smooth(target, 282)
# 240 and 241 are the two sides of a cut (shots 4-8 sit between them): no blending across it
def hold(obj):
    ad = obj.animation_data
    try:
        curves = ad.action.fcurves
    except AttributeError:
        curves = [fc for layer in ad.action.layers for strip in layer.strips for bag in strip.channelbags for fc in bag.fcurves]
    for fc in curves:
        for kp in fc.keyframe_points:
            if abs(kp.co.x - 240) < 0.5: kp.interpolation = "CONSTANT"
hold(cam); hold(target)
scene.camera = cam

scene.frame_set(1)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ROOT, "opendock_ad.blend"))
print("built:", len(scene.objects), "objects; saved", bpy.data.filepath)
