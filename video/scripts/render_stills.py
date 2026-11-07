# Renders single frames of a scene with Cycles (for real glass), in a background Blender.
#   blender -b video/opendock_ad.blend --python video/scripts/render_stills.py -- "<scene>" <w> <h> <samples> <out_dir> <frame> [<frame>...]
import bpy, sys, os
args = sys.argv[sys.argv.index("--") + 1:]
name, w, h, samples, out = args[:5]; frames = [int(f) for f in args[5:]]
s = bpy.data.scenes[name]
s.render.engine = "CYCLES"
prefs = bpy.context.preferences.addons["cycles"].preferences
for kind in ("OPTIX", "CUDA", "HIP", "ONEAPI"):
    try:
        prefs.compute_device_type = kind; prefs.get_devices()
        if any(d.type == kind for d in prefs.devices):
            for d in prefs.devices: d.use = d.type == kind
            s.cycles.device = "GPU"; break
    except Exception: pass
s.cycles.samples = int(samples); s.cycles.use_denoising = True
s.cycles.max_bounces = 12; s.cycles.transmission_bounces = 12
s.render.resolution_x, s.render.resolution_y, s.render.resolution_percentage = int(w), int(h), 100
s.render.image_settings.file_format = "PNG"
for f in frames:
    s.frame_set(f)
    s.render.filepath = os.path.join(out, "c_%04d.png" % f)
    bpy.ops.render.render(write_still=True, scene=s.name)
print("DONE", s.cycles.device)
