import bpy, os
ROOT = os.environ.get("OPENDOCK_VIDEO") or os.path.join(os.path.expanduser("~"), "Desktop", "mydockfinder-opensource", "video")
s = bpy.data.scenes["OpenDock Ad"]; bpy.context.window.scene = s
cam = s.camera
s.frame_set(150)
saved = (cam.constraints[0].mute, tuple(cam.location), tuple(cam.rotation_euler))
# a temporary close-up camera on the keyboard and hinge, then everything back
import mathutils
c2d = bpy.data.cameras.new("Closeup"); c2d.lens = 60
c2 = bpy.data.objects.new("Closeup", c2d); s.collection.objects.link(c2)
c2.location = (-0.20, -0.21, 0.20)
tgt = mathutils.Vector((-0.02, 0.06, 0.02))
c2.rotation_euler = (tgt - c2.location).to_track_quat("-Z", "Y").to_euler()
s.camera = c2
s.render.resolution_percentage = 50
s.render.filepath = os.path.join(ROOT, "renders", "closeup_keys.png")
bpy.ops.render.render(write_still=True)
s.camera = cam
bpy.data.objects.remove(c2, do_unlink=True); bpy.data.cameras.remove(c2d)
s.render.resolution_percentage = 100
print("ok")
