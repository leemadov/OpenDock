# OpenDock wallpapers, rendered with Blender (Cycles). Original artwork, free to use with OpenDock.
#
#   blender --background --factory-startup --python render.py -- <design> <light|dark> <width> <height> <samples> <out.png>
#
# Designs: liquid (iridescent glass ribbons), glass (glass slabs and pills over a
# glow), dunes (sand dunes under a physical sky).
import bpy, math, sys, random
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
design, mode, W, H, samples, out = argv[0], argv[1], int(argv[2]), int(argv[3]), int(argv[4]), argv[5]
dark = mode == "dark"
random.seed(hash(design) & 0xFFFF)

# ---------- scene ----------
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.render.engine = "CYCLES"
prefs = bpy.context.preferences.addons["cycles"].preferences
for kind in ("OPTIX", "CUDA"):
    try:
        prefs.compute_device_type = kind
        prefs.get_devices()
        if any(d.type == kind for d in prefs.devices):
            for d in prefs.devices: d.use = d.type == kind
            scene.cycles.device = "GPU"
            break
    except Exception:
        pass
scene.cycles.samples = samples
scene.cycles.use_denoising = True
scene.cycles.max_bounces = 16
scene.cycles.transmission_bounces = 16
scene.cycles.glossy_bounces = 8
scene.cycles.caustics_reflective = False
scene.cycles.caustics_refractive = False
scene.render.resolution_x, scene.render.resolution_y = W, H
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_depth = "16"
try:
    scene.view_settings.view_transform = "AgX"
    scene.view_settings.look = "AgX - Medium High Contrast"
except Exception:
    pass

def hexcol(h, a=1.0):
    h = h.lstrip("#")
    srgb = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    lin = [c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in srgb]
    return (lin[0], lin[1], lin[2], a)

def principled(name, color="#FFFFFF", rough=0.3, metal=0.0, transmission=0.0, ior=1.45, coat=0.0, sheen=0.0, film=0.0, emission=None, strength=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    def put(key, value):
        if key in b.inputs: b.inputs[key].default_value = value
    put("Base Color", hexcol(color)); put("Roughness", rough); put("Metallic", metal)
    put("Transmission Weight", transmission); put("Transmission", transmission); put("IOR", ior)
    put("Coat Weight", coat); put("Sheen Weight", sheen)
    if film: put("Thin Film Thickness", film); put("Thin Film IOR", 1.33)
    if emission: put("Emission Color", hexcol(emission)); put("Emission Strength", strength)
    return m

def gradient_plane(name, colors, size, loc, rot, strength=1.0, axis="y"):
    """An emissive backdrop with a smooth multi-stop gradient across it."""
    bpy.ops.mesh.primitive_plane_add(size=1, location=loc, rotation=rot)
    p = bpy.context.object; p.name = name; p.scale = size
    m = bpy.data.materials.new(name); m.use_nodes = True
    nt = m.node_tree; nt.nodes.clear()
    tc = nt.nodes.new("ShaderNodeTexCoord"); sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    ramp = nt.nodes.new("ShaderNodeValToRGB"); em = nt.nodes.new("ShaderNodeEmission"); outn = nt.nodes.new("ShaderNodeOutputMaterial")
    nt.links.new(tc.outputs["Generated"], sep.inputs[0])
    nt.links.new(sep.outputs["Y" if axis == "y" else "X"], ramp.inputs[0])
    cr = ramp.color_ramp; cr.interpolation = "B_SPLINE"
    while len(cr.elements) < len(colors): cr.elements.new(0.5)
    for i, c in enumerate(colors):
        cr.elements[i].position = i / (len(colors) - 1); cr.elements[i].color = hexcol(c)
    nt.links.new(ramp.outputs[0], em.inputs[0]); em.inputs[1].default_value = strength
    nt.links.new(em.outputs[0], outn.inputs[0])
    p.data.materials.append(m)
    return p

def world(color, strength):
    w = bpy.data.worlds.new("World"); scene.world = w; w.use_nodes = True
    bg = w.node_tree.nodes["Background"]; bg.inputs[0].default_value = hexcol(color); bg.inputs[1].default_value = strength

def area(loc, rot, size, color, power):
    bpy.ops.object.light_add(type="AREA", location=loc, rotation=rot)
    l = bpy.context.object; l.data.size = size; l.data.color = hexcol(color)[:3]; l.data.energy = power
    return l

def camera(loc, look_at, lens=50, fstop=0.0, focus=None):
    bpy.ops.object.camera_add(location=loc)
    cam = bpy.context.object; scene.camera = cam
    d = Vector(look_at) - Vector(loc); cam.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()
    cam.data.lens = lens
    if fstop:
        cam.data.dof.use_dof = True; cam.data.dof.aperture_fstop = fstop
        cam.data.dof.focus_distance = focus if focus else d.length
    return cam

def smooth(obj):
    for poly in obj.data.polygons: poly.use_smooth = True

def ribbon(points, width, thickness, tilt, mat):
    """A flat, thick ribbon along a smooth curve: extruded and bevelled so its edges are round glass."""
    cd = bpy.data.curves.new("ribbon", "CURVE"); cd.dimensions = "3D"
    sp = cd.splines.new("BEZIER"); sp.bezier_points.add(len(points) - 1)
    for bp, (co, t) in zip(sp.bezier_points, zip(points, tilt)):
        bp.co = co; bp.handle_left_type = bp.handle_right_type = "AUTO"; bp.tilt = t
    cd.extrude = width; cd.bevel_depth = thickness; cd.bevel_resolution = 16; cd.resolution_u = 256; cd.render_resolution_u = 512
    ob = bpy.data.objects.new("ribbon", cd); scene.collection.objects.link(ob)
    ob.data.materials.append(mat)
    return ob

# ---------- designs ----------

def liquid():
    if dark:
        world("#05060F", 0.4)
        gradient_plane("bg", ["#050A24", "#1C0F45", "#3A0D52", "#06243F"], (40, 24, 1), (0, 9, 0), (math.radians(90), 0, 0), 1.0, "x")
        tints = ["#9FE7FF", "#E2B5FF", "#FFC2E2"]
        area((-7, -2, 5), (math.radians(60), 0, math.radians(-60)), 6, "#7B5CFF", 2200)
        area((7, -1, 3), (math.radians(70), 0, math.radians(60)), 6, "#00D1FF", 2200)
        area((0, 6, 6), (math.radians(-40), 0, 0), 8, "#FF4FA3", 1500)
    else:
        world("#FFF6F0", 0.35)
        gradient_plane("bg", ["#FFB48F", "#FF9CC8", "#B79CFF", "#7FC4FF", "#8FF0E0"], (40, 24, 1), (0, 9, 0), (math.radians(90), 0, 0), 1.25, "x")
        tints = ["#FFFFFF", "#FFF0FA", "#EEF6FF"]
        area((-6, -4, 6), (math.radians(55), 0, math.radians(-50)), 8, "#FFFFFF", 1500)
        area((7, -1, 3), (math.radians(70), 0, math.radians(60)), 6, "#7FD8FF", 1400)
        area((0, 6, 6), (math.radians(-40), 0, 0), 8, "#FF9FCB", 900)
    for k in range(3):
        mat = principled("glass%d" % k, tints[k], rough=0.02, transmission=1.0, ior=1.45, film=380 + 90 * k)
        y = 1.2 + k * 1.1; z0 = -1.6 + k * 1.3
        pts = [(-11, y, z0 - 1.2), (-5.5, y - 0.8, z0 + 1.8), (0, y + 0.4, z0 - 0.4), (5, y - 0.6, z0 + 1.6), (11, y, z0 - 0.8)]
        ribbon(pts, 1.1 - k * 0.2, 0.22, [0.2, 1.1, 2.2, 3.0, 3.9][::1 if k % 2 == 0 else -1], mat)
    camera((0, -14, 0.6), (0, 2, 0.2), lens=55, fstop=4.0, focus=15.0)

def glass():
    if dark:
        world("#02040C", 0.3)
        gradient_plane("bg", ["#02102A", "#0B3E7A", "#0A6B8C", "#0A1F5C"], (40, 30, 1), (0, 0, -1.5), (0, 0, 0), 1.4, "x")
    else:
        world("#FFFFFF", 0.7)
        gradient_plane("bg", ["#3FD3C8", "#22A5D6", "#1F6FE0", "#2A3FD0"], (40, 30, 1), (0, 0, -1.5), (0, 0, 0), 1.7, "x")
    mat = principled("glass", "#FFFFFF", rough=0.03, transmission=1.0, ior=1.5)
    frost = principled("frost", "#FFFFFF", rough=0.25, transmission=1.0, ior=1.5)
    def slab(loc, size, rot, m, bevel=0.25):
        bpy.ops.mesh.primitive_cube_add(location=loc, rotation=rot)
        o = bpy.context.object; o.scale = size
        bpy.ops.object.transform_apply(scale=True)
        mod = o.modifiers.new("bevel", "BEVEL"); mod.width = bevel; mod.segments = 12; mod.limit_method = "NONE"
        o.data.materials.append(m); smooth(o)
        return o
    # a long glass bar like a dock, tiles on it, a pill floating above
    slab((0, 0, -0.9), (6.2, 1.15, 0.22), (0, 0, math.radians(-8)), mat, 0.2)
    for i, x in enumerate((-4.2, -1.4, 1.4, 4.2)):
        slab((x, 0.15 - i * 0.18, -0.35), (0.95, 0.95, 0.16), (math.radians(4), 0, math.radians(-8)), mat if i % 2 == 0 else frost, 0.35)
    slab((-0.6, 2.6, 0.9), (1.4, 0.5, 0.35), (math.radians(10), 0, math.radians(-14)), mat, 0.34)
    bpy.ops.mesh.primitive_uv_sphere_add(radius=0.55, location=(3.4, 2.2, 0.8), segments=64, ring_count=32)
    s = bpy.context.object; s.scale = (1, 1, 0.55); s.data.materials.append(mat); smooth(s)
    area((-5, -5, 7), (math.radians(40), 0, math.radians(-40)), 6, "#FFFFFF", 1500)
    area((6, 4, 5), (math.radians(-35), 0, math.radians(140)), 5, "#B8F0FF" if not dark else "#5AD8FF", 900)
    camera((0, -9.5, 6.5), (0, 0.4, -0.6), lens=42, fstop=2.8, focus=11.2)

def dunes():
    bpy.ops.mesh.primitive_plane_add(size=120, location=(0, 30, 0))
    p = bpy.context.object
    sub = p.modifiers.new("sub", "SUBSURF"); sub.subdivision_type = "SIMPLE"; sub.levels = 7; sub.render_levels = 8
    tex = bpy.data.textures.new("dune", "CLOUDS"); tex.noise_scale = 14.0; tex.noise_depth = 1
    disp = p.modifiers.new("disp", "DISPLACE"); disp.texture = tex; disp.strength = 9.0; disp.mid_level = 0.5
    tex2 = bpy.data.textures.new("ripple", "STUCCI"); tex2.noise_scale = 0.35; tex2.turbulence = 2
    d2 = p.modifiers.new("ripple", "DISPLACE"); d2.texture = tex2; d2.strength = 0.08
    smooth(p)
    sand = principled("sand", "#C98A55" if not dark else "#8C6A55", rough=0.9, sheen=0.4)
    p.data.materials.append(sand)
    bpy.ops.mesh.primitive_plane_add(size=3000, location=(0, 1500, -0.5))
    far = bpy.context.object
    fs = far.modifiers.new("sub", "SUBSURF"); fs.subdivision_type = "SIMPLE"; fs.levels = 6; fs.render_levels = 7
    ft = bpy.data.textures.new("far", "CLOUDS"); ft.noise_scale = 60.0
    fd = far.modifiers.new("disp", "DISPLACE"); fd.texture = ft; fd.strength = 14.0; fd.mid_level = 0.6
    smooth(far); far.data.materials.append(sand)
    w = bpy.data.worlds.new("Sky"); scene.world = w; w.use_nodes = True
    nt = w.node_tree; sky = nt.nodes.new("ShaderNodeTexSky")
    for t in ("MULTIPLE_SCATTERING", "NISHITA"):
        try: sky.sky_type = t; break
        except Exception: pass
    sky.sun_elevation = math.radians(-1.5 if dark else 3.5); sky.sun_rotation = math.radians(200)
    try: sky.air_density = 1.4; sky.dust_density = 2.5 if not dark else 1.0
    except Exception: pass
    nt.links.new(sky.outputs[0], nt.nodes["Background"].inputs[0])
    nt.nodes["Background"].inputs[1].default_value = 2.2 if dark else 0.3
    bpy.ops.object.light_add(type="SUN", rotation=(math.radians(84 if not dark else 90), 0, math.radians(20)))
    sun = bpy.context.object
    if dark: sun.rotation_euler = (math.radians(55), 0, math.radians(-30)); sun.data.energy = 0.35; sun.data.color = hexcol("#9DB8FF")[:3]
    else: sun.rotation_euler = (math.radians(84), 0, math.radians(35)); sun.data.energy = 6.5; sun.data.color = hexcol("#FFA25E")[:3]
    sun.data.angle = math.radians(1.0)
    camera((0, -26, 7.5), (0, 18, 0.5), lens=45, fstop=0)


# ---------- macOS-style scenes ----------

def heightfield(name, nx, ny, x0, x1, y0, y1, fn, mat=None):
    """A grid surface z = fn(x, y), smooth-shaded."""
    import bmesh
    me = bpy.data.meshes.new(name); bm = bmesh.new(); rows = []
    for j in range(ny + 1):
        y = y0 + (y1 - y0) * j / ny
        rows.append([bm.verts.new((x0 + (x1 - x0) * i / nx, y, fn(x0 + (x1 - x0) * i / nx, y))) for i in range(nx + 1)])
    for j in range(ny):
        for i in range(nx):
            bm.faces.new((rows[j][i], rows[j][i + 1], rows[j + 1][i + 1], rows[j + 1][i]))
    bm.to_mesh(me); bm.free()
    ob = bpy.data.objects.new(name, me); scene.collection.objects.link(ob); smooth(ob)
    if mat: ob.data.materials.append(mat)
    return ob

def bands_material(name, color, rough, scale, strength, sheen=0.3):
    """A matte material with fine flowing striations (a wave-texture bump), like brushed clay or sand."""
    m = principled(name, color, rough=rough, sheen=sheen)
    nt = m.node_tree; b = nt.nodes["Principled BSDF"]
    tc = nt.nodes.new("ShaderNodeTexCoord"); wave = nt.nodes.new("ShaderNodeTexWave"); bump = nt.nodes.new("ShaderNodeBump")
    wave.wave_type = "BANDS"; wave.bands_direction = "Z"; wave.inputs["Scale"].default_value = scale
    wave.inputs["Distortion"].default_value = 3.0; wave.inputs["Detail"].default_value = 2.0
    nt.links.new(tc.outputs["Object"], wave.inputs["Vector"]); nt.links.new(wave.outputs["Fac"], bump.inputs["Height"])
    bump.inputs["Strength"].default_value = strength; nt.links.new(bump.outputs["Normal"], b.inputs["Normal"])
    return m

def sky_node(nt, elev, rot):
    sky = nt.nodes.new("ShaderNodeTexSky")
    for t in ("MULTIPLE_SCATTERING", "NISHITA"):
        try:
            sky.sky_type = t
            break
        except Exception:
            pass
    sky.sun_elevation = math.radians(elev); sky.sun_rotation = math.radians(rot)
    return sky

def stars_world(sky_strength):
    """Night sky: the physical sky after dusk, plus stars."""
    w = bpy.data.worlds.new("Night"); scene.world = w; w.use_nodes = True
    nt = w.node_tree; bg = nt.nodes["Background"]; out = nt.nodes["World Output"]
    sky = sky_node(nt, -7, 250)
    nt.links.new(sky.outputs[0], bg.inputs[0]); bg.inputs[1].default_value = sky_strength
    tc = nt.nodes.new("ShaderNodeTexCoord"); vor = nt.nodes.new("ShaderNodeTexVoronoi"); vor.inputs["Scale"].default_value = 420
    nt.links.new(tc.outputs["Generated"], vor.inputs["Vector"])
    mr = nt.nodes.new("ShaderNodeMapRange")
    mr.inputs[1].default_value = 0.0; mr.inputs[2].default_value = 0.035; mr.inputs[3].default_value = 1.0; mr.inputs[4].default_value = 0.0
    nt.links.new(vor.outputs["Distance"], mr.inputs[0])
    sep = nt.nodes.new("ShaderNodeSeparateColor"); nt.links.new(vor.outputs["Color"], sep.inputs[0])
    gate = nt.nodes.new("ShaderNodeMath"); gate.operation = "GREATER_THAN"; gate.inputs[1].default_value = 0.72
    nt.links.new(sep.outputs[0], gate.inputs[0])
    mul = nt.nodes.new("ShaderNodeMath"); mul.operation = "MULTIPLY"
    nt.links.new(mr.outputs[0], mul.inputs[0]); nt.links.new(gate.outputs[0], mul.inputs[1])
    em = nt.nodes.new("ShaderNodeEmission"); em.inputs[0].default_value = (1, 1, 1, 1); nt.links.new(mul.outputs[0], em.inputs[1])
    add = nt.nodes.new("ShaderNodeAddShader"); nt.links.new(bg.outputs[0], add.inputs[0]); nt.links.new(em.outputs[0], add.inputs[1])
    nt.links.new(add.outputs[0], out.inputs[0])

def day_world(elev, rot, strength):
    w = bpy.data.worlds.new("Day"); scene.world = w; w.use_nodes = True
    nt = w.node_tree; sky = sky_node(nt, elev, rot)
    nt.links.new(sky.outputs[0], nt.nodes["Background"].inputs[0]); nt.nodes["Background"].inputs[1].default_value = strength

def dune():
    """A field of sculpted dunes: the big one in front seen along its crest, which sweeps away in an S to a peak --
    its gentle windward face glowing in low sun on the left, its steep slip face in shadow on the right -- smaller
    dunes behind it, and a far mountain range."""
    def profile(x, y, ox, oy, H, L, bend):
        cx = ox + 0.30 * (y - oy) + bend * math.sin(0.1 * (y - oy) + 0.2)
        d = x - cx; h = H * math.exp(-((y - oy - L * 0.4) / L) ** 2)
        w = 11.0 * H / 14.0; f = 6.0 * H / 14.0
        if d < 0: return h * math.exp(-((d / w) ** 2) * 1.3)
        return h * max(0.0, 1 - d / f) ** 1.75
    field = ((0, 2, 16.0, 22.0, 6.0), (32, 10, 8.0, 16.0, 3.0), (-38, 22, 7.0, 15.0, 3.5), (12, 52, 10.0, 18.0, 4.0), (-14, 70, 7.5, 16.0, 3.0))
    def z(x, y):
        v = max(profile(x, y, *dn) for dn in field)
        return v - 0.5 + 0.35 * math.sin(0.09 * x + 0.5) * math.cos(0.07 * y) + 0.04 * math.sin(2.1 * y + 0.8 * x)
    sand = bands_material("sand", "#D48C4A" if not dark else "#9AACD2", 0.97, 1.6, 0.09, 0.0)
    heightfield("dunes", 760, 860, -80, 70, -95, 110, z, sand)
    heightfield("plain", 160, 60, -260, 260, 100, 200, lambda x, y: -0.5 + 1.2 * math.sin(0.05 * x) * math.cos(0.04 * y), sand)
    def ridge(x, y):
        v = 0.0
        for f, a, ph in ((0.022, 22, 0.3), (0.06, 9.0, 1.9), (0.15, 3.5, 0.7), (0.38, 1.4, 2.2)):
            v += a * (1 - abs(math.sin(f * x + ph + 0.01 * y)))
        t = (y - 200) / 60.0
        return v * (0.6 + 0.4 * math.sin(0.012 * x + 1.4)) * math.sin(math.pi * min(1, max(0, t))) - 3
    rock = principled("rock", "#7A5F52" if not dark else "#2E395E", rough=0.9)
    heightfield("mountains", 480, 60, -320, 320, 200, 260, ridge, rock)
    if dark:
        stars_world(1.6)
        bpy.ops.object.light_add(type="SUN", rotation=(0, math.radians(-68), math.radians(-42)))
        moon = bpy.context.object; moon.data.energy = 1.4; moon.data.color = hexcol("#B4C6FF")[:3]; moon.data.angle = math.radians(0.6)
    else:
        day_world(9, 270, 0.3)
        bpy.ops.object.light_add(type="SUN", rotation=(0, math.radians(-77), math.radians(-42)))
        sun = bpy.context.object; sun.data.energy = 4.0; sun.data.color = hexcol("#FFAA66")[:3]; sun.data.angle = math.radians(0.7)
    camera((6, -74, 3.5), (1, 12, 8.5), lens=50)

def strata():
    """Rounded hills rising in layers toward the back, finely striated like brushed clay, one in a bright accent."""
    cols = ["#121316", "#24272C", "#5E636B", "#A9AEB5", "#FF7A3C", "#D3D6DB"] if dark else ["#BDB5AA", "#D9D2C8", "#EAE5DE", "#F6F3EF", "#7FA3E6", "#FFFFFF"]
    world("#050506" if dark else "#F4F1EC", 0.2 if dark else 0.8)
    gradient_plane("bg", ["#2A1A10", "#120D0B", "#060607"] if dark else ["#E4DBD0", "#EDE6DD", "#F7F3EE"], (120, 60, 1), (0, 60, 0), (math.radians(90), 0, 0), 0.8 if dark else 1.15, "y")
    for k in range(6):
        depth = 14 - k * 2.6
        cx = (2.5 if k % 2 == 0 else -2.5) * (1 + 0.3 * k) - 1
        hh = 9.5 - 1.35 * k
        def hill(x, y, cx=cx, hh=hh, k=k, depth=depth):
            u = (x - cx) / (8 + 2.2 * (5 - k)); v = (y - depth) / 3.8
            return hh * math.exp(-u * u - v * v) * (1 + 0.1 * math.sin(0.9 * x + k)) - 1.0 + 0.0 * k
        m = bands_material("layer%d" % k, cols[5 - k], 0.6, 3.2, 0.55, 0.45)
        heightfield("hill%d" % k, 340, 140, -32, 32, depth - 10, depth + 10, hill, m)
    area((-14, -10, 16), (math.radians(40), 0, math.radians(-45)), 18, "#FFFFFF", 4200 if dark else 4800)
    area((14, 30, 4), (math.radians(-80), 0, math.radians(160)), 14, "#FF9A5A" if dark else "#FFE2C6", 2400)
    camera((0, -30, 1.2), (0, 8, 3.6), lens=60)

def drift():
    """Soft satin hills filling the foreground, deeply out of focus, and a clear glass wave curling up across a sky
    that warms toward the horizon -- only the wave's edge is sharp."""
    bg_cols = ["#F2DDA0", "#C4D3EA", "#5A8FDA", "#2C64BE"] if not dark else ["#5A3570", "#28327E", "#0F205C", "#060D2E"]
    world("#B8CCEA" if not dark else "#0A1030", 0.5)
    gradient_plane("bg", bg_cols, (90, 60, 1), (0, 34, 2), (math.radians(90), 0, 0), 1.3 if not dark else 1.0, "y")
    blues = ["#1A47C8", "#3A74E4", "#A9C6EE", "#2340A6", "#5E8FE6"] if not dark else ["#1B2C8A", "#3B2B9E", "#2A3FB0", "#5A3FC0", "#24328F"]
    shapes = (((-6.5, -6.0, -2.4), (5.0, 3.0, 3.6)), ((5.5, -5.5, -2.6), (5.0, 3.4, 4.0)), ((-0.5, -2.0, -2.8), (7.0, 3.0, 3.2)),
              ((-12, 5, -1.8), (7, 3, 2.8)), ((11, 4, -2.0), (7, 3, 3.0)))
    for i, (loc, sc) in enumerate(shapes):
        bpy.ops.mesh.primitive_uv_sphere_add(radius=1, location=loc, segments=96, ring_count=48)
        o = bpy.context.object; o.scale = sc; smooth(o)
        o.data.materials.append(principled("satin%d" % i, blues[i], rough=0.4, sheen=0.8, coat=0.25))
    sheet = principled("sheet", "#9FD8FF" if not dark else "#7FC8FF", rough=0.14, transmission=1.0, ior=1.45, film=520)
    ribbon([(-20, 8.0, -4.0), (-9, 8.5, -1.5), (-1, 9.0, 2.2), (7, 9.5, 5.6), (19, 10.0, 7.2)], 4.2, 0.06, [0.2, 0.9, 1.6, 2.1, 2.5], sheet)
    area((-8, -10, 12), (math.radians(40), 0, math.radians(-30)), 14, "#FFFFFF", 2600 if not dark else 1500)
    area((4, 20, 6), (math.radians(-80), 0, math.radians(180)), 18, "#FFF1C8" if not dark else "#8FDFFF", 3200)
    camera((0, -15, 1.6), (1, 6, 0.6), lens=46, fstop=1.4, focus=25.0)

def fold():
    """Layered paper waves sweeping corner to corner, each layer casting a soft shadow on the next."""
    import bmesh
    cols = (["#FFFBF5", "#FDF8F1", "#FBF6EE", "#F6EEE3", "#ECE2D5", "#E0D6CD", "#D3CBD0", "#C4BFCF", "#B4B3CB", "#A4A8C5", "#959DBF", "#8791B6", "#7A86AE"] if not dark
            else ["#625B6B", "#5C5664", "#56505E", "#4A4552", "#3E3B4D", "#343447", "#2C2E43", "#25293F", "#1F2439", "#191E33", "#151A2E", "#12162A", "#0F1326"])
    world("#F2EEEA" if not dark else "#0B0C14", 0.45 if not dark else 0.2)
    papers = []
    for idx, k in enumerate(range(-5, 8)):
        me = bpy.data.meshes.new("paper%d" % idx); bm = bmesh.new()
        top, bot = [], []
        n = 360
        for i in range(n + 1):
            x = -60 + 120 * i / n
            zt = 6 - 2.0 * k + 2.4 * math.sin(0.16 * x + 0.5 * k) + 0.7 * math.sin(0.42 * x - 0.35 * k)
            top.append(bm.verts.new((x, 0, zt))); bot.append(bm.verts.new((x, 0, -60)))
        for i in range(n):
            bm.faces.new((bot[i], bot[i + 1], top[i + 1], top[i]))
        bm.to_mesh(me); bm.free()
        o = bpy.data.objects.new("paper%d" % idx, me); scene.collection.objects.link(o)
        o.location = (0, -0.45 * idx, 0)
        sol = o.modifiers.new("solid", "SOLIDIFY"); sol.thickness = 0.08
        bev = o.modifiers.new("bevel", "BEVEL"); bev.width = 0.035; bev.segments = 4; bev.limit_method = "ANGLE"
        o.data.materials.append(principled("p%d" % idx, cols[idx], rough=0.7, sheen=0.4))
        smooth(o); papers.append(o)
    root = bpy.data.objects.new("root", None); scene.collection.objects.link(root)
    for o in papers: o.parent = root
    root.rotation_euler = (0, math.radians(-30), 0)
    area((-14, -16, 16), (math.radians(38), 0, math.radians(-40)), 5, "#FFFFFF", 4600 if not dark else 2400)
    area((16, -12, -6), (math.radians(115), 0, math.radians(55)), 14, "#FFE3C4" if not dark else "#8C9CFF", 900)
    camera((2, -44, 2), (2, 0, 2), lens=55)

def flux():
    """Liquid chrome flowing in broad swirls, its iridescent surface catching wide bands of coloured light."""
    def z(x, y):
        wx = x + 2.2 * math.sin(0.22 * y + 0.9 * math.sin(0.17 * x))
        wy = y + 1.8 * math.sin(0.2 * x + 0.7 * math.cos(0.15 * y))
        return 1.2 * math.sin(0.34 * wx) * math.cos(0.29 * wy) + 0.4 * math.sin(0.75 * wx + 0.5 * wy)
    chrome = principled("chrome", "#F4F4F8", rough=0.1, metal=1.0, film=360)
    heightfield("liquid", 640, 380, -24, 24, -14, 14, z, chrome)
    world("#000000" if dark else "#F3F5FB", 0.0 if dark else 0.75)
    cols = ["#FF4FA0", "#3F7BFF", "#7FE7FF", "#FFB050"]
    for i, c in enumerate(cols):
        bpy.ops.mesh.primitive_plane_add(size=1, location=(-12 + i * 8, -3 + (i % 2) * 7, 10),
                                         rotation=(math.radians(12 * (i % 2 * 2 - 1)), 0, math.radians(25 + 32 * i)))
        strip = bpy.context.object; strip.scale = (2.6, 60, 1)
        strip.data.materials.append(principled("light%d" % i, c, emission=c, strength=7 if dark else 5))
        strip.visible_camera = False
    camera((0, -0.01, 26), (0, 0, 0), lens=34)

{"liquid": liquid, "glass": glass, "dunes": dunes, "dune": dune, "strata": strata, "drift": drift, "fold": fold, "flux": flux}[design]()
scene.render.filepath = out
bpy.ops.render.render(write_still=True)
print("RENDERED", out)
