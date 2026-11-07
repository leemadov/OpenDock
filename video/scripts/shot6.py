import os
# OpenDock ad, shot 6 (Finder): the laptop's screen (the desktop with the pink Dock) on a flat panel; the Finder
# window swings in at an angle and lands on it, then its folders turn pink one by one (an image sequence made by
# finder_recolor.py). The camera starts on shot 5's last view and pulls back to the whole screen.
# Runs inside Blender (video/bl.py). Scale: 1 m = 1000 screen DIPs; the screen is 2.56 x 1.44 m, centred at 0.
import bpy, math, os

ROOT = os.environ.get("OPENDOCK_VIDEO") or os.path.join(os.path.expanduser("~"), "Desktop", "mydockfinder-opensource", "video")
A = os.path.join(ROOT, "assets")
FRAMES = 90

scene = bpy.data.scenes.get("Shot 6")
if scene is None:
    scene = bpy.data.scenes.new("Shot 6")
else:
    for o in list(scene.objects):
        bpy.data.objects.remove(o, do_unlink=True)
bpy.context.window.scene = scene
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x, scene.render.resolution_y = 1920, 1080
scene.render.fps = 30
scene.frame_start, scene.frame_end = 1, FRAMES
scene.view_settings.view_transform = "Standard"     # interface colours exactly as drawn
scene.view_settings.look = "None"
scene.view_settings.exposure = 0
scene.render.use_motion_blur = True
scene.render.motion_blur_shutter = 0.5
world = bpy.data.worlds.get("Shot 6 World") or bpy.data.worlds.new("Shot 6 World")
world.use_nodes = True
world.node_tree.nodes["Background"].inputs["Color"].default_value = (0, 0, 0, 1)
scene.world = world

def link(o):
    scene.collection.objects.link(o)
    return o

def ease_keys(obj, kind="BEZIER"):
    ad = obj.animation_data
    if not ad or not ad.action: return
    try:
        curves = ad.action.fcurves
    except AttributeError:
        curves = [fc for layer in ad.action.layers for strip in layer.strips for bag in strip.channelbags for fc in bag.fcurves]
    for fc in curves:
        for k in fc.keyframe_points:
            k.interpolation = kind; k.handle_left_type = k.handle_right_type = "AUTO_CLAMPED"

def flat_material(name, image, fade=False):
    # an image shown as it is (emission, Standard view), with its own alpha; `fade` adds an animatable opacity
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    try: m.surface_render_method = "BLENDED"
    except Exception: m.blend_method = "BLEND"
    nt = m.node_tree
    for n in list(nt.nodes): nt.nodes.remove(n)
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    tex = nt.nodes.new("ShaderNodeTexImage"); tex.image = image; tex.interpolation = "Cubic"
    em = nt.nodes.new("ShaderNodeEmission"); em.inputs["Strength"].default_value = 1.0
    tr = nt.nodes.new("ShaderNodeBsdfTransparent")
    mix = nt.nodes.new("ShaderNodeMixShader")
    nt.links.new(tex.outputs["Color"], em.inputs["Color"])
    alpha = tex.outputs["Alpha"]
    if fade:
        mul = nt.nodes.new("ShaderNodeMath"); mul.operation = "MULTIPLY"; mul.name = "Fade"
        mul.inputs[1].default_value = 1.0
        nt.links.new(alpha, mul.inputs[0]); alpha = mul.outputs[0]
    nt.links.new(alpha, mix.inputs["Fac"])
    nt.links.new(tr.outputs[0], mix.inputs[1]); nt.links.new(em.outputs[0], mix.inputs[2])
    nt.links.new(mix.outputs[0], out.inputs["Surface"])
    return m, tex

def plane(name, w, h, material):
    bpy.ops.mesh.primitive_plane_add(size=1)
    o = bpy.context.active_object
    for c in o.users_collection: c.objects.unlink(o)
    scene.collection.objects.link(o)
    o.name = name; o.scale = (w, h, 1)
    o.data.materials.append(material)
    return o

# ---------- the screen: the desktop with the pink Dock ----------
desk_img = bpy.data.images.load(os.path.join(A, "desk_pink.png"), check_existing=True); desk_img.reload()
desk_m, _ = flat_material("Shot6 Desk", desk_img)
desk = plane("Desk", 2.56, 1.44, desk_m)

# ---------- the Finder window: an image sequence, swinging in ----------
seq = bpy.data.images.load(os.path.join(A, "finder_seq", "w_0001.png"), check_existing=True)
seq.source = "SEQUENCE"; seq.reload()
win_m, win_tex = flat_material("Shot6 Finder", seq, fade=True)
win_tex.image_user.frame_duration = FRAMES; win_tex.image_user.frame_start = 1; win_tex.image_user.frame_offset = 0
win_tex.image_user.use_auto_refresh = True
pivot = link(bpy.data.objects.new("Finder Pivot", None))      # the window turns about its own centre
win = plane("Finder", 1.22, 0.80, win_m)                       # 1100 x 680 window + its 60 DIP shadow margin, at 2x
win.parent = pivot
fade = win_m.node_tree.nodes["Fade"].inputs[1]
final = (0.0, 0.03, 0.004)
def key(frame, loc, rot_y, scale, alpha):
    pivot.location = loc; pivot.rotation_euler = (math.radians(4) * (rot_y / 50.0), math.radians(rot_y), 0)
    pivot.scale = (scale, scale, scale)
    pivot.keyframe_insert("location", frame=frame); pivot.keyframe_insert("rotation_euler", frame=frame); pivot.keyframe_insert("scale", frame=frame)
    fade.default_value = alpha; fade.keyframe_insert("default_value", frame=frame)
# (the window lands a quarter bigger than its 1100 x 680 render: a roomy window on a 2560 x 1440 screen)
key(1, (-0.90, -0.05, 0.25), 58, 1.00, 0.0)
key(6, (-0.78, -0.03, 0.22), 52, 1.05, 0.0)
key(16, (-0.26, 0.02, 0.10), 20, 1.18, 1.0)
key(26, (0.01, 0.03, 0.008), -2.5, 1.257, 1.0)   # a touch past, then settles
key(34, final, 0, 1.25, 1.0)
key(FRAMES, final, 0, 1.25, 1.0)
ease_keys(pivot)

# ---------- the camera: from shot 5's last view out to the whole screen ----------
cx, cy, vw, vh = [float(v) for v in open(os.path.join(A, "desk_camera.txt")).read().split()]
cam_d = bpy.data.cameras.new("Shot6 Camera"); cam_d.lens = 50; cam_d.sensor_width = 36; cam_d.sensor_fit = "HORIZONTAL"
cam_d.clip_start = 0.01
cam = link(bpy.data.objects.new("Shot6 Camera", cam_d))
dist = lambda width: width * cam_d.lens / cam_d.sensor_width
cam.location = ((cx - 1280) / 1000, (720 - cy) / 1000, dist(vw / 1000)); cam.keyframe_insert("location", frame=1)
cam.location = (0, 0, dist(2.56)); cam.keyframe_insert("location", frame=34)
ease_keys(cam)
scene.camera = cam

scene.frame_set(1)
bpy.ops.wm.save_mainfile()
print("shot 6 built; camera starts at", tuple(round(v, 3) for v in cam.location))
