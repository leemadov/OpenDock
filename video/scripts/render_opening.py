# Renders shots 1-2 (frames 1-180) to PNG frames at the size given, in a background Blender.
#   blender -b video/opendock_ad.blend --python video/scripts/render_opening.py -- 1280 720 out_dir
import bpy, sys, os
w, h, out = sys.argv[sys.argv.index("--") + 1:][:3]
s = bpy.data.scenes["OpenDock Ad"]
bpy.context.window_manager.windows[0].scene = s if bpy.context.window_manager.windows else s
s.render.resolution_x, s.render.resolution_y, s.render.resolution_percentage = int(w), int(h), 100
s.render.image_settings.file_format = "PNG"
try: s.eevee.taa_render_samples = 48
except Exception: pass
s.render.filepath = os.path.join(out, "f_")
bpy.ops.render.render(animation=True, scene=s.name)
print("DONE")
