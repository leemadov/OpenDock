import bpy, os
FRAMES = (241, 282, 330, 420)
ROOT = os.environ.get("OPENDOCK_VIDEO") or os.path.join(os.path.expanduser("~"), "Desktop", "mydockfinder-opensource", "video")
scene = bpy.data.scenes["OpenDock Ad"]
bpy.context.window.scene = scene
scene.render.resolution_percentage = 50
scene.render.image_settings.file_format = "PNG"
try: scene.eevee.taa_render_samples = 32
except Exception: pass
for f in FRAMES:
    scene.frame_set(f)
    scene.render.filepath = os.path.join(ROOT, "renders", "preview_f%03d.png" % f)
    bpy.ops.render.render(write_still=True)
scene.render.resolution_percentage = 100
print("rendered")
