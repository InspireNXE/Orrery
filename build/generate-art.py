"""Draws Orrery's icons and the setup wizard's side image from the logo in MainWindow.axaml.

Run from the repository root: python build/generate-art.py (needs Pillow). Writes
src/Orrery.App/Assets/orrery.{png,ico,icns}, installer/linux/icons/<size>.png and installer/media/WizardImage.png.
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ACCENT = (0x5B, 0x9D, 0xFF)
TEXT = (0xE6, 0xE8, 0xEC)
PANEL = (0x14, 0x16, 0x1B)
WIZARD_FIELD = (0x23, 0x45, 0x78)  # A deep shade of the accent, as Prober's setup uses its brand colour.
ROOT = Path(__file__).resolve().parent.parent


def draw_tile(size: int) -> Image.Image:
    """The app tile: a dark rounded square with the logo's orbits, sun and planets (a 24-unit canvas)."""
    unit = size * 0.78 / 24
    off = (size - 24 * unit) / 2

    def p(x, y):
        return off + x * unit, off + y * unit

    tile = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(tile).rounded_rectangle([0, 0, size - 1, size - 1], radius=size * 0.22, fill=PANEL + (255,))

    def orbit(cx, cy, w, h, width, alpha):
        # Drawn level, then turned 25 degrees anticlockwise about the centre, like RotateTransform -25.
        layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        ImageDraw.Draw(layer).ellipse([*p(cx - w / 2, cy - h / 2), *p(cx + w / 2, cy + h / 2)],
                                      outline=ACCENT + (alpha,), width=round(width * unit))
        tile.alpha_composite(layer.rotate(25, resample=Image.BICUBIC, center=p(cx, cy)))

    def dot(cx, cy, diameter, colour):
        r = diameter / 2
        ImageDraw.Draw(tile).ellipse([*p(cx - r, cy - r), *p(cx + r, cy + r)], fill=colour + (255,))

    orbit(12, 12, 20, 8.4, 1.4, 128)
    orbit(12, 12, 12.4, 5.2, 1.4, 255)
    dot(12, 12, 5.2, ACCENT)
    dot(21.06, 7.77, 3.2, TEXT)
    dot(6.38, 14.62, 2.4, TEXT)
    return tile


def icons():
    tile = draw_tile(1024)
    base = ROOT / "src/Orrery.App/Assets/orrery"
    base.parent.mkdir(parents=True, exist_ok=True)
    tile.resize((512, 512), Image.LANCZOS).save(f"{base}.png")
    tile.resize((256, 256), Image.LANCZOS).save(f"{base}.ico", sizes=[(s, s) for s in (16, 24, 32, 48, 64, 128, 256)])
    tile.save(f"{base}.icns")

    # The Linux packages install these into the hicolor icon theme, one file per size.
    linux = ROOT / "installer/linux/icons"
    linux.mkdir(parents=True, exist_ok=True)
    for size in (16, 24, 32, 48, 64, 128, 256, 512):
        tile.resize((size, size), Image.LANCZOS).save(linux / f"{size}.png")


def wizard_image():
    """430x824, the size Prober's setup uses: the tile on the accent field, with a soft shadow."""
    width, height, tile_size = 430, 824, 190
    art = Image.new("RGBA", (width, height), WIZARD_FIELD + (255,))
    left, top = (width - tile_size) // 2, (height - tile_size) // 2

    shadow = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle([left, top + 10, left + tile_size, top + tile_size + 10],
                                             radius=tile_size * 0.22, fill=(0, 0, 0, 110))
    art.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(10)))
    art.alpha_composite(draw_tile(tile_size * 4).resize((tile_size, tile_size), Image.LANCZOS), (left, top))

    out = ROOT / "installer/media/WizardImage.png"
    out.parent.mkdir(parents=True, exist_ok=True)
    art.save(out)


if __name__ == "__main__":
    icons()
    wizard_image()
    print("Wrote the icons and the wizard image.")
