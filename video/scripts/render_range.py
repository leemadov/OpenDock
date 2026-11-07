# Renders frames a..b of a scene of opendock_ad.blend to PNGs, in a background Blender.
#   blender -b video/opendock_ad.blend --python video/scripts/render_range.py -- "<scene>" <a> <b> <width> <height> <out_dir>
import bpy, sys, os
name, a, b, w, h, out = sys.argv[sys.argv.index("--") + 1:][:6]
s = bpy.data.scenes[name]
s.frame_start, s.frame_end = int(a), int(b)
s.render.resolution_x, s.render.resolution_y, s.render.resolution_percentage = int(w), int(h), 100
s.render.image_settings.file_format = "PNG"
try: s.eevee.taa_render_samples = 48
except Exception: pass
s.render.filepath = os.path.join(out, "f_")
bpy.ops.render.render(animation=True, scene=s.name)
print("DONE")
