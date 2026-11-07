import bpy
s = bpy.data.scenes["OpenDock Ad"]
print(s.view_settings.view_transform, s.view_settings.look, s.view_settings.exposure, s.world.name, s.world.node_tree.nodes["Background"].inputs["Strength"].default_value)
print([ (o.name, round(o.data.energy)) for o in s.objects if o.type=="LIGHT"])
print("cyc mat", s.objects["Cyclorama"].data.materials[0].name, list(s.objects["Cyclorama"].data.materials[0].node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value))
