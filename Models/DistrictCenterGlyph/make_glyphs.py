# Builds the faction glyph overlays for district centers: just the glyph
# faces of each faction's District Center, pushed out a hair so they sit on
# top of the originals. The game draws the District Center with one merged
# material, so the mod tints this overlay to show the owner's color.
#
#   python ../PowerExchange/extract_game_models.py <Timberborn data dir> <models dir> \
#       DistrictCenter.Folktails.Model DistrictCenter.IronTeeth.Model
#   blender --background --python make_glyphs.py -- <models dir> <out dir>
#
# Needs the Timbermesh Blender plugin installed.

import os
import sys

import bmesh
import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "PowerExchange"))
import timbermesh_import  # noqa: E402

argv = sys.argv[sys.argv.index("--") + 1:]
MODELS_DIR, OUT_DIR = argv[0], argv[1]

FACTIONS = ("Folktails", "IronTeeth")
GLYPH_MATERIAL = "Details."
OFFSET = 0.004


def plugin_modules():
    import addon_utils
    for mod in addon_utils.modules():
        if mod.__name__ == "timbermesh_blender_plugin":
            sys.path.insert(0, os.path.dirname(mod.__file__))
    import model_pb2
    import timbermesh_exporter
    return model_pb2, timbermesh_exporter


model_pb2, timbermesh_exporter = plugin_modules()


def build(faction):
    col = bpy.data.collections.new(f"DistrictCenterGlyph.{faction}.Model")
    bpy.context.scene.collection.children.link(col)
    with open(os.path.join(MODELS_DIR, f"DistrictCenter.{faction}.Model.timbermesh"), "rb") as f:
        objs = timbermesh_import.import_model(f.read(), col, model_pb2)
    base = next(o for o in objs.values() if o.type == "MESH")
    for obj in list(objs.values()):
        if obj is not base:
            bpy.data.objects.remove(obj)
    base.name = f"DistrictCenterGlyph.{faction}"

    bm = bmesh.new()
    bm.from_mesh(base.data)
    glyph = {i for i, m in enumerate(base.data.materials) if m.name.startswith(GLYPH_MATERIAL)}
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.material_index not in glyph], context="FACES")
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
    for face in bm.faces:
        for vert in face.verts:
            vert.co += face.normal * OFFSET
    bm.to_mesh(base.data)
    bm.free()
    print(faction, "glyph faces", len(base.data.polygons))
    return col


def main():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    for faction in FACTIONS:
        col = build(faction)
        settings = timbermesh_exporter.ExportSettings(bpy.context, True, True, False)
        timbermesh_exporter.Exporter.export_collection(
            col, os.path.join(OUT_DIR, f"DistrictCenterGlyph.{faction}.Model.timbermesh"), settings)


if __name__ == "__main__":
    main()
