# Builds the Toll Tube Station model from the game's Tubeway Station and
# Small Warehouse, and exports it as a Timbermesh file.
#
#   python ../PowerExchange/extract_game_models.py <Timberborn data dir> <models dir> \
#       TubewayStation.IronTeeth.Model SmallWarehouse.IronTeeth.Model
#   blender --background --python make_toll_tube_station.py -- <models dir> <out dir>
#
# Needs the Timbermesh Blender plugin installed.
#
# The station keeps the Tubeway Station's shape (3 wide, 2 deep), with a
# small warehouse on its back corner for the toll goods, and a slot where
# idle workers wait. The importer only brings in meshes, so the station's
# animations are copied over after export.

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
STATION = "TubewayStation.IronTeeth.Model"
# The warehouse, scaled down onto a back corner of the roof
WAREHOUSE_AT = (-2.85, -1.9, 1.06)
WAREHOUSE_SCALE = 0.62


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
    col = bpy.data.collections.new("TollTubeStation.IronTeeth.Model")
    bpy.context.scene.collection.children.link(col)
    station = load(STATION, col)
    base = station[STATION]
    base.name = "TollTubeStation.IronTeeth"

    warehouse = load("SmallWarehouse.IronTeeth.Model", col)["SmallWarehouse.IronTeeth.Model"]
    bpy.context.view_layer.update()
    warehouse.data.transform(warehouse.matrix_world)
    warehouse.data.transform(Matrix.Translation((0.5, 0.5, 0)))
    warehouse.data.transform(Matrix.Translation(WAREHOUSE_AT) @ Matrix.Scale(WAREHOUSE_SCALE, 4))
    warehouse.matrix_world = Matrix.Identity(4)
    warehouse.data.transform(Matrix.Translation(-base.location))
    warehouse.location = base.location
    for o in bpy.context.view_layer.objects:
        o.select_set(o in (base, warehouse))
    bpy.context.view_layer.objects.active = base
    bpy.ops.object.join()

    # Where idle workers wait, by the door
    slot = bpy.data.objects.new("#Slot#Entrance", None)
    slot.location = (-1.5, -0.2, 0.02)
    col.objects.link(slot)
    slot.parent = base
    slot.matrix_parent_inverse = base.matrix_world.inverted()
    return col


def copy_animations(path):
    with open(os.path.join(MODELS_DIR, STATION + ".timbermesh"), "rb") as f:
        original = model_pb2.Model()
        original.ParseFromString(f.read())
    with open(path, "rb") as f:
        data = f.read()
    model = model_pb2.Model()
    model.ParseFromString(zlib.decompress(data) if data[:2] == b"x\x9c" else data)
    targets = {n.name.split(".")[0]: n for n in model.nodes}
    for source in original.nodes:
        target = targets.get(source.name.split(".")[0])
        if target is None or not source.nodeAnimations:
            continue
        del target.nodeAnimations[:]
        target.nodeAnimations.extend(source.nodeAnimations)
    with open(path, "wb") as f:
        f.write(zlib.compress(model.SerializeToString()))


def main():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    col = build()
    if os.environ.get("PREVIEW"):
        return
    settings = timbermesh_exporter.ExportSettings(bpy.context, True, True, False)
    path = os.path.join(OUT_DIR, "TollTubeStation.IronTeeth.Model.timbermesh")
    timbermesh_exporter.Exporter.export_collection(col, path, settings)
    copy_animations(path)


if __name__ == "__main__":
    main()
