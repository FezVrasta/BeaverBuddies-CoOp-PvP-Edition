# Builds the Power Exchange models from the game's District Crossing and
# exports them as Timbermesh files.
#
#   python extract_game_models.py <Timberborn data dir> <models dir> \
#       DistrictCrossing.Folktails.Model DistrictCrossing.IronTeeth.Model \
#       GearLarge.Folktails.Model GearLarge.IronTeeth.Model
#   blender --background --python make_power_exchange.py -- <models dir> <out dir>
#
# Needs the Timbermesh Blender plugin installed.
#
# One half of the pair is 3 wide (x from -3 to 0), 1 deep (y from -1 to 0,
# entrance side at y = 0) and 2 tall, the same as the District Crossing. The
# crates and shelves under its two side porches make way for large shaft
# gears, one per port, that turn while power flows.

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
PORT_Y, PORT_Z = -0.55, 0.5
# Slightly smaller than a shaft's gear, to clear the porch posts
GEAR_SCALE = Matrix.Scale(0.9, 4)
# Side porches of the District Crossing, where the shaft ports are
BAYS = ((-2.92, -2.3), (-0.7, -0.08))


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
    """Crates, sacks and the shelf under a side porch."""
    lo, hi = bounds(obj)
    if not any(lo.x >= a and hi.x <= b for a, b in BAYS) or lo.y < -0.92 or hi.z > 0.95:
        return False
    materials = {obj.data.materials[p.material_index].name.split(".")[0] for p in obj.data.polygons}
    if materials == {"Details"}:
        return True
    return hi.z <= 0.41 and materials <= {"BaseWood_White", "BaseWood_Indigo"}


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


def gear_mesh(name, faction, collection):
    """The rest pose mesh of a game gear, centered on its block."""
    objs = load(f"{name}.{faction}.Model", collection, prefix="tmp_")
    node = next(o for o in objs.values() if o.type == "MESH")
    mesh = node.data.copy()
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

    # Large shaft gears in the porches, axles out to the ports. They share
    # one axis, so one node turns both.
    large = gear_mesh("GearLarge", faction, col)  # axle along +y
    origin = (-1.5, PORT_Y, PORT_Z)
    left = place(large, "#PortGears", col,
                 Matrix.Translation((-2.5, PORT_Y, PORT_Z)) @ Matrix.Rotation(math.pi / 2, 4, "Z") @ GEAR_SCALE, origin)
    right = place(large, "PortRight", col,
                  Matrix.Translation((-0.5, PORT_Y, PORT_Z)) @ Matrix.Rotation(-math.pi / 2, 4, "Z") @ GEAR_SCALE, origin)
    select_only([left, right], left)
    bpy.ops.object.join()
    spin(left, 0)

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
