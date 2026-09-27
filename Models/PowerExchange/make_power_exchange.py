# Builds the Power Exchange models from the game's District Crossing and
# exports them as Timbermesh files.
#
#   python extract_game_models.py <Timberborn data dir> <models dir> \
#       DistrictCrossing.Folktails.Model DistrictCrossing.IronTeeth.Model \
#       AxleHorizontal.Folktails.Model AxleHorizontal.IronTeeth.Model \
#       ShaftFrame.Folktails.Model ShaftFrame.IronTeeth.Model
#   blender --background --python make_power_exchange.py -- <models dir> <out dir>
#
# Needs the Timbermesh Blender plugin installed.
#
# One half of the pair is 3 wide (x from -3 to 0), 1 deep (y from -1 to 0,
# entrance side at y = 0) and 2 tall, the same as the District Crossing. The
# crates and shelf under one side porch make way for a piece of the game's
# power shaft, running from the port on the outer face into the building and
# turning while power flows. The other porch keeps its goods.

import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import timbermesh_import  # noqa: E402

argv = sys.argv[sys.argv.index("--") + 1:]
MODELS_DIR, OUT_DIR = argv[0], argv[1]

FACTIONS = ("Folktails", "IronTeeth")
FRAMES = 48  # one turn every two seconds at 24 fps
# The shaft port block, x from -1 to 0 (the game's x = 0 column, whose
# transput faces left). A shaft axle sits at the block's middle height.
PORT_BLOCK = (-0.5, -0.5, 0.0)
# The District Crossing's side porch over that block
BAY = (-0.8, -0.05)


def plugin_modules():
    import addon_utils
    for mod in addon_utils.modules():
        if mod.__name__ == "timbermesh_blender_plugin":
            sys.path.insert(0, os.path.dirname(mod.__file__))
    import model_pb2
    import timbermesh_exporter
    return model_pb2, timbermesh_exporter


model_pb2, timbermesh_exporter = plugin_modules()


def load(name, collection, prefix=""):
    with open(os.path.join(MODELS_DIR, name + ".timbermesh"), "rb") as f:
        return timbermesh_import.import_model(f.read(), collection, model_pb2, prefix)


def select_only(objs, active):
    for o in bpy.context.view_layer.objects:
        o.select_set(o in objs)
    bpy.context.view_layer.objects.active = active


def bounds(obj):
    pts = [obj.matrix_world @ Vector(c) for c in obj.bound_box]
    return (Vector([min(p[i] for p in pts) for i in range(3)]),
            Vector([max(p[i] for p in pts) for i in range(3)]))


def is_porch_prop(obj):
    """Crates, sacks and the shelf under the port's porch, and the shelf lip in the shaft's way."""
    lo, hi = bounds(obj)
    if lo.x < BAY[0] or hi.x > BAY[1] or lo.y < -0.92 or hi.z > 0.95:
        return False
    materials = {obj.data.materials[p.material_index].name.split(".")[0] for p in obj.data.polygons}
    if materials == {"Details"}:
        return True
    if hi.z <= 0.41 and materials <= {"BaseWood_White", "BaseWood_Indigo"}:
        return True
    return lo.z >= 0.39 and hi.z <= 0.54 and lo.y < PORT_BLOCK[1] < hi.y


def strip_porches(base):
    bpy.context.view_layer.update()
    select_only([base], base)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.separate(type="LOOSE")
    bpy.ops.object.mode_set(mode="OBJECT")
    parts = [p for p in bpy.context.selected_objects if p is not base]
    props = [p for p in parts if is_porch_prop(p)]
    keep = [p for p in parts if p not in props]
    for prop in props:
        bpy.data.objects.remove(prop)
    select_only(keep + [base], base)
    bpy.ops.object.join()
    return len(props)


def part_mesh(name, faction, collection):
    """The rest pose mesh of a game model's first mesh node, in model space."""
    objs = load(f"{name}.{faction}.Model", collection, prefix="tmp_")
    bpy.context.view_layer.update()
    node = next(o for o in objs.values() if o.type == "MESH")
    mesh = node.data.copy()
    mesh.transform(node.matrix_world)
    for o in objs.values():
        bpy.data.objects.remove(o)
    return mesh


def place(mesh, name, collection, matrix, origin):
    """A copy of mesh moved by matrix, with its object origin at origin."""
    data = mesh.copy()
    data.transform(Matrix.Translation(-Vector(origin)) @ matrix)
    obj = bpy.data.objects.new(name, data)
    obj.location = origin
    collection.objects.link(obj)
    return obj


def spin(obj, axis):
    obj.rotation_mode = "XYZ"
    obj.rotation_euler = (0, 0, 0)
    obj.keyframe_insert("rotation_euler", index=axis, frame=0)
    rot = [0, 0, 0]
    rot[axis] = 2 * math.pi
    obj.rotation_euler = rot
    obj.keyframe_insert("rotation_euler", index=axis, frame=FRAMES)


def build(faction):
    col = bpy.data.collections.new(f"PowerExchange.{faction}.Model")
    bpy.context.scene.collection.children.link(col)
    objs = load(f"DistrictCrossing.{faction}.Model", col)
    base = objs[f"DistrictCrossing.{faction}"]
    base.name = f"PowerExchange.{faction}"
    print(faction, "removed", strip_porches(base), "porch parts")

    # A power shaft through the port block, the same as the game's straight
    # shaft along x: the axle, and the bearing frame on the outer edge where
    # the other shaft connects
    to_port = Matrix.Translation(PORT_BLOCK) @ Matrix.Rotation(math.pi / 2, 4, "Z")
    frame = place(part_mesh("ShaftFrame", faction, col), "Frame", col, to_port, (0, 0, 0))
    select_only([base, frame], base)
    bpy.ops.object.join()
    axle_origin = (PORT_BLOCK[0], PORT_BLOCK[1], 0.5)
    axle = place(part_mesh("AxleHorizontal", faction, col), "#Shaft", col, to_port, axle_origin)
    spin(axle, 0)
    return col


def main():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    bpy.context.preferences.edit.keyframe_new_interpolation_type = "LINEAR"
    scene = bpy.context.scene
    scene.render.fps = 24
    scene.frame_start, scene.frame_end = 0, FRAMES
    for faction in FACTIONS:
        col = build(faction)
        settings = timbermesh_exporter.ExportSettings(bpy.context, True, True, False)
        timbermesh_exporter.Exporter.export_collection(
            col, os.path.join(OUT_DIR, f"PowerExchange.{faction}.Model.timbermesh"), settings)


if __name__ == "__main__":
    main()
