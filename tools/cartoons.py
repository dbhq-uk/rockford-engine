#!/usr/bin/env python3
"""A world's cartoons as a strip the front end can play.

The VGA release keeps each shipped world's cartoons in `<WORLD>.CCM`: a full 320 by
200 screen, six frames across and four rows down, 48 pixels each. The first row is
the introduction, which the original never plays; the other three are `WIN`, `LOSE`
and `TIME`, which it does - see docs/FORMATS.md, "When each cartoon plays". A
`.CCM` has no palette of its own and uses its world's `.SCM`.

This writes the three that play as one PNG, 288 by 144: six frames across, and the
rows in the order `WIN`, `LOSE`, `TIME`, which is the order `CartoonKind` numbers
them. Every pixel is kept, because a cartoon frame has its own background.

    python3 tools/cartoons.py --selftest
"""
import sys

from PIL import Image

FRAME = 48
FRAMES = 6
ROWS_PLAYED = 3          # the sheet's rows 2 to 4; row 1 is never played
SKIPPED_ROWS = 1
WIDTH = 320


def strip(pixels, palette):
    """Rows 2 to 4 of a 320 by 200 indexed screen, as RGBA."""
    out = Image.new('RGBA', (FRAME * FRAMES, FRAME * ROWS_PLAYED))
    put = out.load()
    for y in range(FRAME * ROWS_PLAYED):
        start = (FRAME * SKIPPED_ROWS + y) * WIDTH
        for x in range(FRAME * FRAMES):
            r, g, b = palette[pixels[start + x]]
            put[x, y] = (r, g, b, 255)
    return out


def selftest():
    # Each cell of the 4 by 6 grid is filled with its own index, so a strip that
    # took the wrong row or the wrong column shows up as the wrong colour.
    pixels = [0] * (WIDTH * 200)
    for row in range(4):
        for col in range(FRAMES):
            for y in range(FRAME):
                for x in range(FRAME):
                    pixels[(row * FRAME + y) * WIDTH + col * FRAME + x] = 1 + row * FRAMES + col
    palette = [(i, 255 - i, 7) for i in range(256)]

    img = strip(pixels, palette)
    assert img.size == (288, 144), img.size

    # Row 2 of the sheet is the strip's first row: cells 7 to 12.
    assert img.getpixel((0, 0)) == (7, 248, 7, 255), img.getpixel((0, 0))
    assert img.getpixel((5 * FRAME, 0)) == (12, 243, 7, 255)
    # Row 4 of the sheet is the strip's last row: cells 19 to 24.
    assert img.getpixel((0, 143)) == (19, 236, 7, 255)
    assert img.getpixel((287, 143)) == (24, 231, 7, 255)
    # Row 1 - the introduction - is nowhere in it.
    colours = {img.getpixel((x, y))[0] for x in range(0, 288, 7) for y in range(0, 144, 7)}
    assert not colours & set(range(1, 7)), sorted(colours & set(range(1, 7)))
    print('cartoons: selftest ok')


if __name__ == '__main__':
    if '--selftest' in sys.argv:
        selftest()
    else:
        print(__doc__)
