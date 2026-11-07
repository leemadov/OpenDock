# Renders frames a..b of a scene with Cycles (real glass) to PNGs, in a background Blender.
#   blender -b video/opendock_ad.blend --python video/scripts/render_range_cycles.py -- "<scene>" <a> <b> <w> <h> <samples> <out_dir>
import bpy, sys, os
name, a, b, w, h, samples, out = sys.argv[sys.argv.index("--") + 1:][:7]
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
s.frame_start, s.frame_end = int(a), int(b)
s.render.resolution_x, s.render.resolution_y, s.render.resolution_percentage = int(w), int(h), 100
s.render.use_motion_blur = True
s.render.image_settings.file_format = "PNG"
s.render.filepath = os.path.join(out, "f_")
bpy.ops.render.render(animation=True, scene=s.name)
print("DONE")
