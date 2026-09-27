# Builds the lighting masks for the district center glyph overlays: white on
# the glyph's background, black on the faction symbol and everywhere else,
# laid out like the Details texture the glyph is mapped onto. The mod lights
# the overlay through this mask, so only the background takes the owner's
# color.
#
#   python make_masks.py <Details_D.Folktails.png> <Details_D.IronTeeth.png> <out dir>
#
# Needs Pillow. The Details textures come from the game's resources.assets.

import sys

from PIL import Image, ImageFilter

# The glyph's area in the Details texture (uv0 of the glyph faces)
U0, U1, V0, V1 = 0.05, 0.45, 0.55, 0.95
SIZE = 512


def symbol_weight(r, g, b, faction):
    """How much a pixel belongs to the symbol, from 0 to 1."""
    if faction == "IronTeeth":
        # Red symbol on dark blue
        value = (r - max(g, b) - 40) / 60
    else:
        # Yellow symbol on dark green
        value = (min(r, g) - b - 20) / 40
    return max(0.0, min(1.0, value))


def build(source, faction, out_dir):
    image = Image.open(source).convert("RGB").resize((SIZE, SIZE), Image.LANCZOS)
    mask = Image.new("L", (SIZE, SIZE), 0)
    x0, x1 = int(U0 * SIZE), int(U1 * SIZE)
    y0, y1 = int((1 - V1) * SIZE), int((1 - V0) * SIZE)
    pixels, out = image.load(), mask.load()
    for y in range(y0, y1):
        for x in range(x0, x1):
            out[x, y] = int(255 * (1 - symbol_weight(*pixels[x, y], faction)))
    mask = mask.filter(ImageFilter.GaussianBlur(0.6))
    mask.save(f"{out_dir}/DistrictCenterGlyphMask.{faction}.png")


def main(folktails, iron_teeth, out_dir):
    build(folktails, "Folktails", out_dir)
    build(iron_teeth, "IronTeeth", out_dir)


if __name__ == "__main__":
    main(*sys.argv[1:4])
