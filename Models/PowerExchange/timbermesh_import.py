# Imports Timbermesh models into Blender, the reverse of the Timbermesh
# Blender plugin's exporter (same axis swap, winding and vertex layers), so
# game models can be reused as a base and exported again.

import struct

import bpy
from mathutils import Quaternion, Vector


def _floats(prop, count):
    dim = prop.scalarTypeDimension
    values = struct.unpack(f"<{count * dim}f", prop.data)
    return [values[i * dim:(i + 1) * dim] for i in range(count)]


def _material(name):
    return bpy.data.materials.get(name) or bpy.data.materials.new(name)


def import_model(data, collection, model_pb2, prefix=""):
    """Creates one object per Timbermesh node in collection, returns them by node name."""
    model = model_pb2.Model()
    model.ParseFromString(data)
    objects = []
    for node in model.nodes:
        name = prefix + node.name
        if node.vertexCount == 0:
            obj = bpy.data.objects.new(name, None)
        else:
            props = {p.name: _floats(p, node.vertexCount) for p in node.vertexProperties}
            verts = [(-p[0], -p[2], p[1]) for p in props["position"]]
            faces, face_mats, materials = [], [], []
            for mesh in node.meshes:
                materials.append(mesh.material)
                idx = list(mesh.indices)
                for i in range(0, len(idx), 3):
                    faces.append((idx[i + 2], idx[i + 1], idx[i]))
                    face_mats.append(len(materials) - 1)
            me = bpy.data.meshes.new(name)
            me.from_pydata(verts, [], faces)
            for m in materials:
                me.materials.append(_material(m))
            for poly, mi in zip(me.polygons, face_mats):
                poly.material_index = mi
            loops = [v for f in faces for v in f]
            for layer in ("uv0", "uv1", "uv2"):
                if layer in props:
                    uv = me.uv_layers.new(name=layer)
                    for li, vi in enumerate(loops):
                        uv.data[li].uv = props[layer][vi][:2]
            if "color" in props:
                col = me.vertex_colors.new(name="Col")
                for li, vi in enumerate(loops):
                    col.data[li].color = props["color"][vi]
            if "normal" in props:
                normals = [(-n[0], -n[2], n[1]) for n in props["normal"]]
                me.normals_split_custom_set([normals[vi] for vi in loops])
            me.update()
            obj = bpy.data.objects.new(name, me)
        p, r, s = node.position, node.rotation, node.scale
        obj.location = (-p.x, -p.z, p.y)
        obj.rotation_mode = "QUATERNION"
        obj.rotation_quaternion = Quaternion((r.w, r.x, r.z, -r.y))
        obj.scale = (s.x, s.z, s.y)
        collection.objects.link(obj)
        objects.append((node, obj))
    for node, obj in objects:
        if node.parent >= 0:
            obj.parent = objects[node.parent][1]
    return {node.name: obj for node, obj in objects}
