"""Generate the three Thunderstore package icons (256x256 PNG).

Deliberately geometric rather than illustrative. These need to read as intentional
at 64px in a mod list, not to be artwork. Each package shares a ground and frame so
they look like a family, with one distinct mark so they are tellable apart:

    Core     a filled centre block          the board it tunes
    Editor   one lit cell plus a caret      editing a single value
    Cheats   a slash across the grid        it steps outside the rules

Palette matches docs/modding-surface.html, so the repo, the audit and the packages
all look like one project.

Regenerate with:

    py -3 tools/make-icons.py

Placeholders by intent: swap in real art before a wide release, but these are good
enough to publish with.
"""

import os

from PIL import Image, ImageDraw

SIZE = 256

# Same tokens as the audit document's dark theme.
GROUND = (16, 22, 20)
FRAME = (58, 71, 68)
ACCENT = (69, 197, 177)
MUTED = (42, 53, 50)
WARN = (223, 172, 76)

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

CELL = 34
GAP = 10
ORIGIN = 48
STRIDE = CELL + GAP


def base():
    """Shared ground and frame."""
    img = Image.new("RGBA", (SIZE, SIZE), GROUND)
    draw = ImageDraw.Draw(img)
    draw.rectangle([6, 6, SIZE - 7, SIZE - 7], outline=FRAME, width=3)
    return img, draw


def grid(draw, lit=None, lit_colour=ACCENT):
    """A 4x4 board. `lit` holds (col, row) pairs drawn in the accent colour."""
    lit = lit or set()
    for row in range(4):
        for col in range(4):
            x = ORIGIN + col * STRIDE
            y = ORIGIN + row * STRIDE
            on = (col, row) in lit
            draw.rectangle([x, y, x + CELL, y + CELL],
                           fill=lit_colour if on else MUTED)


def core():
    img, draw = base()
    grid(draw, lit={(1, 1), (2, 1), (1, 2), (2, 2)})
    return img


def editor():
    """One cell lit, with its value being dragged out as a bar.

    An earlier version used a thin caret, which vanished at list size and left this
    looking like Core with fewer cells lit. The bar survives downscaling and says
    "a value, being set" rather than just "a cell".
    """
    img, draw = base()
    grid(draw, lit={(0, 1)})

    x = ORIGIN + STRIDE
    y = ORIGIN + STRIDE
    mid = y + CELL // 2

    # Track across the remaining three columns, then the filled portion over it.
    track_end = ORIGIN + 3 * STRIDE + CELL
    draw.rectangle([x, mid - 5, track_end, mid + 5], fill=MUTED)
    draw.rectangle([x, mid - 5, x + int((track_end - x) * 0.55), mid + 5], fill=ACCENT)

    # Handle at the end of the filled portion.
    hx = x + int((track_end - x) * 0.55)
    draw.rectangle([hx - 7, mid - 18, hx + 7, mid + 18], fill=ACCENT)
    return img


def cheats():
    img, draw = base()
    grid(draw, lit={(0, 3), (3, 0)}, lit_colour=WARN)
    draw.line([44, SIZE - 44, SIZE - 44, 44], fill=WARN, width=8)
    return img


TARGETS = [
    ("ComboMod", core),
    ("ComboMod-Editor", editor),
    ("ComboMod-Cheats", cheats),
]


def main():
    for name, make in TARGETS:
        out_dir = os.path.join(ROOT, "packaging", name)
        os.makedirs(out_dir, exist_ok=True)
        path = os.path.join(out_dir, "icon.png")
        make().save(path, "PNG")
        print("wrote", os.path.relpath(path, ROOT), os.path.getsize(path), "bytes")


if __name__ == "__main__":
    main()
