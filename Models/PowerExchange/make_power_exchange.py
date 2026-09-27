# Builds the Power Exchange models and exports them as Timbermesh files.
#
#   blender --background --python Models/PowerExchange/make_power_exchange.py -- <example.blend> <out dir> [preview.png]
#
# <example.blend> is TimberbornExampleModels.blend from the game's
# StreamingAssets/Modding folder, used for the Folktails materials in the
# preview render. Needs the Timbermesh Blender plugin installed.
#
# One half of the pair: 3 wide (x from -3 to 0), 1 deep (y from -1 to 0, the
# entrance side is y = 0 and the other half is behind y = -1), 2 tall. The
# middle column is the walkway, the side columns hold the shaft ports, and
# the upper middle block is the gearbox. The big gear sits against the back
# edge so the two halves' gears meet at the border.

import math
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

argv = sys.argv[sys.argv.index("--") + 1:]
EXAMPLE, OUT_DIR = argv[0], argv[1]
PREVIEW = argv[2] if len(argv) > 2 else None

FACTIONS = {
    "Folktails": dict(
        wood="BaseWood_Brown.Folktails",
        light="BaseWood_LightBrown.Folktails",
        planks="IrregularPlanks_Brown.Folktails",
        metal="BaseMetal.Folktails",
        paint="PaintedMetal.Folktails",
        roof="ThatchedRoof.Folktails",
    ),
    "IronTeeth": dict(
        wood="BaseWood_DarkBrown.IronTeeth",
        light="BaseWood_Grey.IronTeeth",
        planks="IrregularPlanks_Grey.IronTeeth",
        metal="BaseMetal.IronTeeth",
        paint="PaintedMetal.IronTeeth",
        roof="RoofPlanks.IronTeeth",
    ),
}

FRAMES = 48  # one turn every two seconds at 24 fps
GEAR_Z = 1.56  # big gear axle height
SIDE_Z = 0.5  # shaft port height
AXLE_Y = -0.5


def material(name):
    mat = bpy.data.materials.get(name)
    if mat is None:
        mat = bpy.data.materials.new(name)
    return mat


def new_object(name, bm, collection, mats, origin=(0, 0, 0)):
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    for m in mats:
        mesh.materials.append(material(m))
    obj = bpy.data.objects.new(name, mesh)
    collection.objects.link(obj)
    if origin != (0, 0, 0):
        mesh.transform(Matrix.Translation(-Vector(origin)))
        obj.location = origin
    return obj


class Builder:
    """Collects boxes, cylinders and gears into one bmesh per material."""

    def __init__(self, mats):
        self.bm = bmesh.new()
        self.mats = list(mats)

    def _tag(self, faces, mat):
        index = self.mats.index(mat)
        for f in faces:
            f.material_index = index

    def box(self, lo, hi, mat):
        lo, hi = Vector(lo), Vector(hi)
        res = bmesh.ops.create_cube(self.bm, size=1.0)
        verts = res["verts"]
        for v in verts:
            v.co = Vector((
                lo.x + (v.co.x + 0.5) * (hi.x - lo.x),
                lo.y + (v.co.y + 0.5) * (hi.y - lo.y),
                lo.z + (v.co.z + 0.5) * (hi.z - lo.z),
            ))
        self._tag({f for v in verts for f in v.link_faces}, mat)

    def cylinder(self, center, axis, radius, length, mat, segments=12):
        res = bmesh.ops.create_cone(self.bm, cap_ends=True, segments=segments,
                                    radius1=radius, radius2=radius, depth=length)
        rot = {
            "x": Matrix.Rotation(math.pi / 2, 4, "Y"),
            "y": Matrix.Rotation(math.pi / 2, 4, "X"),
            "z": Matrix.Identity(4),
        }[axis]
        bmesh.ops.transform(self.bm, matrix=Matrix.Translation(center) @ rot, verts=res["verts"])
        self._tag({f for v in res["verts"] for f in v.link_faces}, mat)

    def gear(self, center, radius, width, teeth, mat, hub_mat, spokes=4):
        """A spoked wooden gear turning around the x axis."""
        center = Vector(center)
        rim = 0.12 * radius + 0.03
        # Rim as a ring of boxes, one per tooth, so it stays low poly
        for i in range(teeth):
            a = 2 * math.pi * i / teeth
            self._radial_box(center, a, radius - rim, radius, width, 2 * math.pi * radius / teeth * 1.08, mat)
            self._radial_box(center, a, radius, radius + 0.07, width * 0.8, 2 * math.pi * radius / teeth * 0.45, mat)
        for i in range(spokes):
            a = math.pi * i / spokes
            self._radial_box(center, a, -(radius - rim), radius - rim, width * 0.6, 0.06, mat)
        self.cylinder(center, "x", 0.07, width * 1.6, hub_mat, segments=8)

    def beam(self, p0, p1, size, mat):
        """A square beam from p0 to p1."""
        p0, p1 = Vector(p0), Vector(p1)
        d = p1 - p0
        res = bmesh.ops.create_cube(self.bm, size=1.0)
        verts = res["verts"]
        for v in verts:
            v.co = Vector((v.co.x * size, v.co.y * size, (v.co.z + 0.5) * d.length))
        rot = d.normalized().to_track_quat("Z", "Y").to_matrix().to_4x4()
        bmesh.ops.transform(self.bm, matrix=Matrix.Translation(p0) @ rot, verts=verts)
        self._tag({f for v in verts for f in v.link_faces}, mat)

    def gable(self, x0, x1, y0, y1, z0, z1, mat, overhang=0.06):
        """A roof with its ridge along x."""
        ym = (y0 + y1) / 2
        pts = [(y0 - overhang, z0), (y1 + overhang, z0), (ym, z1)]
        left = [self.bm.verts.new((x0 - overhang, y, z)) for y, z in pts]
        right = [self.bm.verts.new((x1 + overhang, y, z)) for y, z in pts]
        faces = [self.bm.faces.new(left[::-1]), self.bm.faces.new(right)]
        for i in range(3):
            j = (i + 1) % 3
            faces.append(self.bm.faces.new((left[i], left[j], right[j], right[i])))
        self._tag(faces, mat)

    def _radial_box(self, center, angle, r0, r1, width, span, mat):
        res = bmesh.ops.create_cube(self.bm, size=1.0)
        verts = res["verts"]
        for v in verts:
            # local: x along the axle, y tangential, z radial
            v.co = Vector((v.co.x * width, v.co.y * span, r0 + (v.co.z + 0.5) * (r1 - r0)))
        bmesh.ops.transform(self.bm, matrix=Matrix.Translation(center) @ Matrix.Rotation(angle, 4, "X"), verts=verts)
        self._tag({f for v in verts for f in v.link_faces}, mat)

    def finish(self, name, collection, origin=(0, 0, 0)):
        bmesh.ops.remove_doubles(self.bm, verts=self.bm.verts, dist=0.0001)
        obj = new_object(name, self.bm, collection, self.mats, origin)
        project_uvs(obj)
        return obj


def project_uvs(obj):
    bpy.context.view_layer.objects.active = obj
    for o in bpy.context.view_layer.objects:
        o.select_set(o == obj)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.cube_project(cube_size=1.0)
    bpy.ops.object.mode_set(mode="OBJECT")


def frame_box(b, x0, x1, z0, z1, m, post=0.1, inset=0.04):
    """Four posts and a ring of beams top and bottom, over one column."""
    y0, y1 = -1 + inset, -inset
    for x in (x0 + inset, x1 - inset - post):
        for y in (y0, y1 - post):
            b.box((x, y, z0), (x + post, y + post, z1), m["wood"])
    for z in (z0, z1 - 0.1):
        b.box((x0 + inset, y0, z), (x1 - inset, y0 + 0.08, z + 0.1), m["light"])
        b.box((x0 + inset, y1 - 0.08, z), (x1 - inset, y1, z + 0.1), m["light"])
        b.box((x0 + inset, y0, z), (x0 + inset + 0.08, y1, z + 0.1), m["light"])
        b.box((x1 - inset - 0.08, y0, z), (x1 - inset, y1, z + 0.1), m["light"])


def spin(obj):
    obj.rotation_mode = "XYZ"
    obj.rotation_euler = (0, 0, 0)
    obj.keyframe_insert("rotation_euler", index=0, frame=0)
    obj.rotation_euler = (2 * math.pi, 0, 0)
    obj.keyframe_insert("rotation_euler", index=0, frame=FRAMES)


def build(faction, m):
    col = bpy.data.collections.new(f"PowerExchange.{faction}.Model")
    bpy.context.scene.collection.children.link(col)
    static = Builder(m.values())

    # Plank floor under the whole half
    static.box((-3, -1, 0), (0, 0, 0.05), m["planks"])

    # Low plank sheds on both sides, where the shafts come in
    for x0 in (-3, -1):
        x1 = x0 + 1
        static.box((x0 + 0.08, -0.9, 0.05), (x1 - 0.08, -0.1, 0.78), m["planks"])
        for x in (x0 + 0.04, x1 - 0.16):
            for y in (-0.96, -0.16):
                static.box((x, y, 0.05), (x + 0.12, y + 0.12, 0.82), m["wood"])
        static.box((x0 + 0.04, -0.96, 0.78), (x1 - 0.04, -0.04, 0.86), m["light"])
        static.gable(x0 + 0.04, x1 - 0.04, -0.96, -0.04, 0.86, 1.2, m["roof"])
        # Bearing blocks for the port gears on the outer walls
        xo = x0 + 0.04 if x0 == -3 else x1 - 0.12
        static.box((xo, AXLE_Y - 0.1, SIDE_Z - 0.1), (xo + 0.08, AXLE_Y + 0.1, SIDE_Z + 0.1), m["metal"])

    # Walkway arch and the deck the gear stands on
    for y in (-0.96, -0.16):
        static.box((-2.08, y, 0.9), (-0.92, y + 0.12, 1.02), m["wood"])
    static.box((-2.08, -0.96, 1.02), (-0.92, -0.04, 1.08), m["planks"])

    # Trestles holding the big gear's axle
    for x in (-1.98, -1.14):
        for y in (-0.92, -0.08):
            static.beam((x + 0.06, y, 1.08), (x + 0.06, AXLE_Y, GEAR_Z + 0.06), 0.07, m["wood"])
        static.box((x, AXLE_Y - 0.09, GEAR_Z - 0.09), (x + 0.12, AXLE_Y + 0.09, GEAR_Z + 0.09), m["metal"])
    static.finish("PowerExchange", col)

    # Big gear and its axle, meeting the other half's gear at the border
    main = Builder([m["light"], m["metal"]])
    main.gear((-1.5, AXLE_Y, GEAR_Z), 0.4, 0.12, 16, m["light"], m["metal"], spokes=4)
    main.cylinder(Vector((-1.5, AXLE_Y, GEAR_Z)), "x", 0.045, 0.96, m["metal"], segments=8)
    spin(main.finish("#MainGear", col, origin=(-1.5, AXLE_Y, GEAR_Z)))

    # Port gears on the outer walls, where the shafts connect
    side = Builder([m["light"], m["metal"]])
    for x in (-2.9, -0.1):
        side.gear((x, AXLE_Y, SIDE_Z), 0.27, 0.07, 10, m["light"], m["metal"], spokes=2)
    spin(side.finish("#PortGears", col, origin=(-1.5, AXLE_Y, SIDE_Z)))
    return col


def append_example_materials():
    with bpy.data.libraries.load(EXAMPLE, link=False) as (src, dst):
        dst.materials = [n for n in src.materials if n.endswith(".Folktails")]


def export(col, path):
    import timbermesh_exporter
    settings = timbermesh_exporter.ExportSettings(bpy.context, True, True, False)
    timbermesh_exporter.Exporter.export_collection(col, path, settings)


def render_preview(col, path):
    scene = bpy.context.scene
    for c in scene.collection.children:
        c.hide_render = c != col
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x, scene.render.resolution_y = 900, 700
    scene.frame_set(6)
    world = bpy.data.worlds.new("Preview")
    world.use_nodes = True
    world.node_tree.nodes['Background'].inputs['Strength'].default_value = 1.6
    world.node_tree.nodes['Background'].inputs['Color'].default_value = (0.7, 0.75, 0.8, 1)
    scene.world = world
    # A mirrored second half behind the border, like the game places it
    twin = bpy.data.collections.new("Twin")
    scene.collection.children.link(twin)
    for obj in col.objects:
        copy = obj.copy()
        copy.matrix_world = Matrix.Translation((-3, -2, 0)) @ Matrix.Rotation(math.pi, 4, "Z") @ obj.matrix_world
        if copy.animation_data:
            copy.animation_data.action = obj.animation_data.action
        twin.objects.link(copy)
    ground = bpy.data.meshes.new("Ground")
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=6)
    bm.to_mesh(ground)
    g = bpy.data.objects.new("Ground", ground)
    g.location = (-1.5, -1, -0.01)
    scene.collection.objects.link(g)
    gm = bpy.data.materials.new("Grass")
    gm.diffuse_color = (0.35, 0.5, 0.2, 1)
    gm.use_nodes = True
    gm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.3, 0.45, 0.15, 1)
    ground.materials.append(gm)
    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
    sun.data.energy = 5
    sun.rotation_euler = (math.radians(50), 0, math.radians(35))
    scene.collection.objects.link(sun)
    cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
    cam.data.lens = 40
    scene.collection.objects.link(cam)
    target = Vector((-1.5, -1.0, 0.9))
    cam.location = target + Vector((3.4, 4.4, 4.0))
    cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()
    scene.camera = cam
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def main():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    bpy.context.preferences.edit.keyframe_new_interpolation_type = "LINEAR"
    append_example_materials()
    scene = bpy.context.scene
    scene.render.fps = 24
    scene.frame_start, scene.frame_end = 0, FRAMES
    cols = {f: build(f, m) for f, m in FACTIONS.items()}
    for f, col in cols.items():
        export(col, f"{OUT_DIR}/PowerExchange.{f}.Model.timbermesh")
    if PREVIEW:
        render_preview(cols["Folktails"], PREVIEW)


main()
