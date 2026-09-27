# Extracts game models as Timbermesh files, to use as a base in Blender.
#
#   python extract_game_models.py <Timberborn data dir> <out dir> <model name>...
#
# Needs UnityPy. Game models live in resources.assets as prefabs whose first
# MonoBehaviour holds the zlib-compressed Timbermesh file after a 32 byte
# header and its length.

import os
import struct
import sys
import zlib

import UnityPy


def main(data_dir, out_dir, names):
    env = UnityPy.load(os.path.join(data_dir, "resources.assets"))
    missing = set(names)
    for obj in env.objects:
        if obj.type.name != "GameObject":
            continue
        go = obj.read()
        if go.m_Name not in missing:
            continue
        for component in go.m_Component:
            ptr = component.component if hasattr(component, "component") else component[1]
            reader = ptr.deref()
            if reader.type.name != "MonoBehaviour":
                continue
            raw = reader.get_raw_data()
            if raw[36:38] != b"x\x9c":
                continue
            length = struct.unpack("<I", raw[32:36])[0]
            with open(os.path.join(out_dir, go.m_Name + ".timbermesh"), "wb") as f:
                f.write(zlib.decompress(raw[36:36 + length]))
            missing.discard(go.m_Name)
            break
    if missing:
        sys.exit("Not found: " + ", ".join(sorted(missing)))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3:])
