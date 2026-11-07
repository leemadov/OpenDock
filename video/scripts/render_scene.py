# Renders a scene of opendock_ad.blend to PNG frames, in a background Blender.
#   blender -b video/opendock_ad.blend --python video/scripts/render_scene.py -- "<scene>" <width> <height> <out_dir>
import bpy, sys, os
name, w, h, out = sys.argv[sys.argv.index("--") + 1:][:4]
s = bpy.data.scenes[name]
s.render.resolution_x, s.render.resolution_y, s.render.resolution_percentage = int(w), int(h), 100
s.render.image_settings.file_format = "PNG"
try: s.eevee.taa_render_samples = 48
except Exception: pass
s.render.filepath = os.path.join(out, "f_")
bpy.ops.render.render(animation=True, scene=s.name)
print("DONE")
