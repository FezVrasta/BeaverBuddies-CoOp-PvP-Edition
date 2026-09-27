# Builds the Toll Station model from the game's Zipline Station and Medium
# Warehouse, and exports it as a Timbermesh file.
#
#   python ../PowerExchange/extract_game_models.py <Timberborn data dir> <models dir> \
#       ZiplineStation.Folktails.Model MediumWarehouse.Folktails.Model
#   blender --background --python make_toll_station.py -- <models dir> <out dir>
#
# Needs the Timbermesh Blender plugin installed.
#
# The station sits on top of the warehouse, one floor up, so it's 3 wide,
# 2 deep and 5 tall, and its entrance (on the y = 0 side) needs stairs. The
# warehouse faces the back, out of the stairs' way.

import math
import os
import sys
import zlib

import bpy
from mathutils import Matrix

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "PowerExchange"))
import timbermesh_import  # noqa: E402

argv = sys.argv[sys.argv.index("--") + 1:]
MODELS_DIR, OUT_DIR = argv[0], argv[1]


def plugin_modules():
    import addon_utils
    for mod in addon_utils.modules():
        if mod.__name__ == "timbermesh_blender_plugin":
            sys.path.insert(0, os.path.dirname(mod.__file__))
    import model_pb2
    import timbermesh_exporter
    return model_pb2, timbermesh_exporter


model_pb2, timbermesh_exporter = plugin_modules()


def load(name, collection):
    with open(os.path.join(MODELS_DIR, name + ".timbermesh"), "rb") as f:
        return timbermesh_import.import_model(f.read(), collection, model_pb2)


def build():
    col = bpy.data.collections.new("TollStation.Folktails.Model")
    bpy.context.scene.collection.children.link(col)
    station = load("ZiplineStation.Folktails.Model", col)
    base = station["ZiplineStation.Folktails.Model"]
    base.name = "TollStation.Folktails"
    for obj in station.values():
        if obj.parent is None:
            obj.location.z += 1

    # The warehouse turned around its center, with its door at the back
    warehouse = load("MediumWarehouse.Folktails.Model", col)["MediumWarehouse.Folktails.Model"]
    bpy.context.view_layer.update()
    turn = Matrix.Translation((-1.5, -1.0, 0)) @ Matrix.Rotation(math.pi, 4, "Z") @ Matrix.Translation((1.5, 1.0, 0))
    warehouse.data.transform(warehouse.matrix_world)
    warehouse.data.transform(turn)
    warehouse.matrix_world = Matrix.Identity(4)
    warehouse.data.transform(Matrix.Translation(-base.location))
    warehouse.location = base.location
    for o in bpy.context.view_layer.objects:
        o.select_set(o in (base, warehouse))
    bpy.context.view_layer.objects.active = base
    bpy.ops.object.join()

    # Where idle workers sit, by the door up top
    slot = bpy.data.objects.new("#Slot#Entrance", None)
    slot.location = (-1.5, -0.3, 1.02)
    col.objects.link(slot)
    slot.parent = base
    slot.matrix_parent_inverse = base.matrix_world.inverted()
    return col


def main():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    col = build()
    if os.environ.get("PREVIEW"):
        return
    settings = timbermesh_exporter.ExportSettings(bpy.context, True, True, False)
    path = os.path.join(OUT_DIR, "TollStation.Folktails.Model.timbermesh")
    timbermesh_exporter.Exporter.export_collection(col, path, settings)
    copy_pylon_animation(path)


def copy_pylon_animation(path):
    """
    The importer only brings in meshes, so the pylon wheel loses its spin
    (and the game expects an animator on every zipline tower). Copy the
    game's animation frames over, keeping the wheel where it now sits.
    """
    with open(os.path.join(MODELS_DIR, "ZiplineStation.Folktails.Model.timbermesh"), "rb") as f:
        original = model_pb2.Model()
        original.ParseFromString(f.read())
    with open(path, "rb") as f:
        data = f.read()
    model = model_pb2.Model()
    model.ParseFromString(zlib.decompress(data) if data[:2] == b"x\x9c" else data)
    source = next(n for n in original.nodes if n.name.startswith("#PylonAnimated"))
    target = next(n for n in model.nodes if n.name.startswith("#PylonAnimated"))
    del target.nodeAnimations[:]
    for animation in source.nodeAnimations:
        copy = target.nodeAnimations.add()
        copy.CopyFrom(animation)
        for frame in copy.frames:
            frame.position.CopyFrom(target.position)
    with open(path, "wb") as f:
        f.write(zlib.compress(model.SerializeToString()))


if __name__ == "__main__":
    main()
