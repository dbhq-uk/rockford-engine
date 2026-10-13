#!/usr/bin/env python3
"""The two transformation chains, built from their finished end tiles.

    python3 tools/dissolve.py        prints what each frame is built from

A worm turns a boulder into the anticlockwise enemy and a boulder into treasure,
and the good worm runs both the other way. Each chain is four frames on the
sheet - y=160 for the enemy, y=176 for the treasure - and the 1988 frames
dissolve in place: at x=240 the tile is almost entirely the first object, at 192
almost entirely the second, and the two between are a scatter in which neither
is whole. Measured against the end tiles, that holds in all five worlds.

Drawn by the model, the middle frames came back as both objects side by side,
whole, and a second redraw changed nothing (#117). So they are not drawn. The
atlas builds each one from the two FINISHED end tiles - the redrawn boulder, the
redrawn enemy or treasure - mixed in patches by a fixed noise field, with a gap
along every seam so the middle frames read as breaking up rather than as two
pictures laid over each other. Both chains share the four tiles, so the frames
say how far apart the picture is, not which way it is going.
"""
import random
import sys

from PIL import Image, ImageChops, ImageFilter

TILE, SCALE = 16, 8
SIDE = TILE * SCALE

# The first object and the second, per row - which end each frame is nearest,
# measured on the 1988 tiles in every world.
ENDS = {160: ((192, 64), (0, 64)),     # the anticlockwise enemy, then the boulder
        176: ((0, 64), (256, 128)),    # the boulder, then the treasure
        144: ((64, 64), (256, 128))}   # the clockwise enemy, then the treasure

# THE BLAST TO TREASURE IS THE SAME KIND OF THING. A clockwise enemy that dies
# leaves a three-by-three of these, and its four frames run from the enemy to
# the treasure - 0x11 is 57 to 80 per cent the enemy in hunter, cook and space,
# 0x14 is the treasure roughened. Drawn, its first frame came out half turned
# already, and as the anchor of the four it took the rest with it. Cowboy is
# the exception: its blast is a scatter of specks that is neither object, and
# its drawn frames are right, so it keeps them.
EXCEPT = {'cowboy': {144}}

# How wide the gap along each seam is, in noise levels out of 255, and how
# fine the patches are. The two middle frames are where neither object may
# read whole - the first check found the monkey's face and the whole revolver
# still plain at 224, and the treasure already whole at 208 - so they break up
# into smaller pieces with much wider gaps between them.
_CHAIN = {240: (.15, 22, 5), 224: (.4, 70, 3), 208: (.62, 70, 3), 192: (.87, 22, 5)}
_BLAST = {256: (.05, 22, 5), 272: (.3, 70, 3), 288: (.62, 70, 3), 304: (.92, 22, 5)}

# row -> x -> (share of the second object, gap, grain)
STEPS = {160: _CHAIN, 176: _CHAIN, 144: _BLAST}

# Every tile this can build, in some world. `applies` says which in which.
POSITIONS = {(x, y) for y, steps in STEPS.items() for x in steps}

# THE WORLDS THIS WAS MEASURED ON, and the only ones it applies to. The 1988 five
# dissolve in place from one end tile to the other - checked against the end tiles
# in all five on 30 September 2026. Sahala's hidden worlds were read on 3 October
# and do not all do the same: music's chain tiles are a mottle that looks like
# neither its boulder (a vinyl record) nor its enemy. A hidden world is drawn by
# the model until its chains are measured the same way and added here (#110).
VERIFIED = ('hunter', 'cook', 'cowboy', 'space', 'body')


_measured = {}


def fits(world, row):
    """Whether this world's chain on `row` dissolves like the 1988 ones: its first
    frame is mostly the first end tile and its last mostly the second. Measured on
    the world's own sheet, because a hidden world's chain is not always one picture
    turning into another (music's tiles look like neither end)."""
    key = (world, row)
    if key not in _measured:
        from pathlib import Path

        from PIL import Image

        from tilecmp import similarity
        sheet = Image.open(Path(__file__).resolve().parent.parent
                           / f'web/Rockford.Web/wwwroot/game/{world}.png').convert('RGBA')

        def tile(p):
            return sheet.crop((p[0], p[1], p[0] + TILE, p[1] + TILE))

        first, last = ENDS[row]
        # By share, not by x: the chains run right to left and the blast left to right.
        by_share = sorted(STEPS[row], key=lambda x: STEPS[row][x][0])
        _measured[key] = (similarity(tile((by_share[0], row)), tile(first)) >= .6
                          and similarity(tile((by_share[-1], row)), tile(last)) >= .9)
    return _measured[key]


def applies(world, pos):
    """Whether this world's tile at pos is built here rather than drawn: the five
    1988 worlds, where it was checked by eye, and any other world whose own chain
    measures the same way (luck's and player's do; scuba's, music's and miner's
    do not and are drawn)."""
    if pos not in POSITIONS or pos[1] in EXCEPT.get(world, set()):
        return False
    return world in VERIFIED or fits(world, pos[1])


def noise(seed, scale=5):
    """A smooth random field, the same every build for the same seed."""
    rnd = random.Random(seed)
    w = SIDE // scale + 2
    small = Image.new('L', (w, w))
    small.putdata([rnd.randrange(256) for _ in range(w * w)])
    big = small.resize((SIDE + 2 * scale, SIDE + 2 * scale), Image.BICUBIC)
    return big.crop((scale, scale, SIDE + scale, SIDE + scale))


def frame(first, second, x, y, world):
    """The tile at (x, y): `second` where the noise is under its share, `first`
    elsewhere, and a clear gap along the seam between them."""
    share, gap, grain = STEPS[y][x]
    field = noise(f'{world}-{y}-{grain}', grain)
    cut = int(share * 255)
    to_second = field.point(lambda v: 255 if v < cut else 0)
    seam = field.point(lambda v: 0 if abs(v - cut) < gap else 255).filter(ImageFilter.GaussianBlur(.6))
    out = Image.composite(second, first, to_second)
    out.putalpha(ImageChops.multiply(out.getchannel('A'), seam))
    return out


if __name__ == '__main__':
    if '--help' in sys.argv or '-h' in sys.argv:
        print(__doc__.strip())
        sys.exit(0)
    for y, (a, b) in ENDS.items():
        for x, (share, _, _) in sorted(STEPS[y].items(), reverse=True):
            print(f'{x},{y}: {1 - share:.0%} of {a[0]},{a[1]} and {share:.0%} of {b[0]},{b[1]}')
