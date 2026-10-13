#!/usr/bin/env python3
"""The ten placeholder player sprites, and which world each belongs to.

    python3 tools/placeholders.py

Every shipped sprite sheet carries ten 16 by 32 figures at x = 0, 16, ... 144,
y = 144 that no sprite table points at. The game's own player animation is the
twenty poses at y = 48, so these ten are not it. Ten figures against ten worlds
is not a coincidence: five of them are for worlds the 1988 menu cannot reach.
For music, luck and miner these placeholders are the only artwork that has ever
existed; scuba and player have full tilesets in the November 1987 release.

This writes them out, and settles the order the only way the data allows.

  sprites/placeholders/<sheet>/<index>.png    one figure, from one sheet
  sprites/placeholders/contact.png            all ten from all five sheets

Three findings come out of the run, and each is printed with what proves it:

**No table indexes them.** A sprite table entry is an EGA planar byte offset,
`y * 40 + b`, and the band is `5760 + b`. Searching both unpacked executables
for a run of those finds nothing at 16-pixel pitch. The only table reaching row
144 covers x = 192 upwards, which is art the game does use. So the placeholders
are unreachable by the shipped code, which is what makes them placeholders.

**Each sheet's own world is identifiable.** The sheet's real player at y = 48 is
matched against all ten figures by silhouette overlap and exact pixel agreement.
The best match is that sheet's own world in all five sheets, which fixes five of
the ten positions from the data rather than from reading the art.

**The colours are per sheet, not per world.** The same figure differs between
sheets by 41 to 47 per cent of its colour indices while its silhouette differs by
about 3 per cent. Every sheet holds its own coloured copy of the whole cast. So
there is no copy of a hidden world's figure in that world's own palette waiting
for the right palette to be applied to it, which an earlier note in
docs/PRIOR-ART.md assumed there was.

`SPACE.FIL` is the exception the prior art reports and it holds: the EGA space
sheet has two non-zero pixels in the band where the others have about 2,490. The
VGA `SPACE.SCM` carries the full set.
"""
import os
import struct
import sys

from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from usage import help_and_exit, wants_help  # noqa: E402

# Before anything is read: without the game data (the public repository has none)
# this crashed on `--help` instead of describing itself.
if wants_help():
    help_and_exit(sys.modules[__name__])

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from scmformat import decode

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, 'sprites/placeholders')
GAME = os.path.join(ROOT, 'web/Rockford.Web/wwwroot/game')

WIDTH, HEIGHT = 320, 200
BAND_Y = 144
FIGURE_W, FIGURE_H = 16, 32
FIGURES = 10

# The sprite tables hold EGA planar byte offsets - 40 bytes to a row, eight
# pixels to a byte - so the band's ten origins are these words.
PLANE_STRIDE = 40
BAND_ENTRIES = [BAND_Y * PLANE_STRIDE + 2 * i for i in range(FIGURES)]

# The five shipped sheets, and the raw world each is.
SHEETS = [('hunter', 0), ('cook', 2), ('cowboy', 4), ('space', 6), ('body', 8)]

VGA = os.path.join(ROOT, 'extracted/vga')
EGA = {'HUNTER': 'extracted/ega1/HUNTER.FIL', 'COOK': 'extracted/ega1/COOK.FIL',
       'COWBOY': 'extracted/ega1/COWBOY.FIL', 'SPACE': 'extracted/ega2/SPACE.FIL',
       'BODY': 'extracted/ega2/BODY.FIL'}


def ega_indices(path):
    """A 32,000-byte .FIL to 64,000 palette indices."""
    d = open(os.path.join(ROOT, path), 'rb').read()
    if len(d) != 32000:
        raise SystemExit(f'{path}: expected 32,000 bytes, got {len(d)}')

    px = bytearray(WIDTH * HEIGHT)
    for plane in range(4):
        bit = 1 << plane
        base = plane * 8000
        for i in range(8000):
            byte = d[base + i]
            if not byte:
                continue
            at = i * 8
            for k in range(8):
                if byte & (0x80 >> k):
                    px[at + k] |= bit
    return px


def band(px):
    """The band's indices, row-major, 160 by 32."""
    return bytes(b for y in range(BAND_Y, BAND_Y + FIGURE_H)
                 for b in px[y * WIDTH:y * WIDTH + FIGURE_W * FIGURES])


def no_table_points_at_them():
    """Search both executables for any run indexing the band at 16-pixel pitch."""
    wanted = set(BAND_ENTRIES)
    for name in ('vga', 'ega'):
        img = open(os.path.join(ROOT, f'unpacked/{name}.bin'), 'rb').read()
        runs, run = [], []
        for off in range(len(img) - 1):
            w = struct.unpack_from('<H', img, off)[0]
            if w in wanted and (not run or off - run[-1] == 2):
                run.append(off)
            elif run:
                if len(run) >= 3:
                    runs.append(run)
                run = []
        print(f'  {name}.bin: {len(runs)} runs of three or more band entries')


def figures(sheet):
    """The ten figures from a sheet's PNG, as RGBA crops."""
    im = Image.open(os.path.join(GAME, f'{sheet}.png')).convert('RGBA')
    return [im.crop((i * FIGURE_W, BAND_Y, (i + 1) * FIGURE_W, BAND_Y + FIGURE_H))
            for i in range(FIGURES)]


def players(sheet):
    """That sheet's real player, every pose, at the same size as a figure."""
    import json
    table = json.load(open(os.path.join(GAME, 'sprites.json')))['player']
    im = Image.open(os.path.join(GAME, f'{sheet}.png')).convert('RGBA')
    return [im.crop((x, y, x + FIGURE_W, y + FIGURE_H)) for x, y in table]


def agreement(a, b):
    """Silhouette overlap and exact pixel agreement, both 0 to 1."""
    ra, rb = a.tobytes(), b.tobytes()
    pa = [ra[i:i + 4] for i in range(0, len(ra), 4)]
    pb = [rb[i:i + 4] for i in range(0, len(rb), 4)]
    sa = [p[3] > 0 for p in pa]
    sb = [p[3] > 0 for p in pb]
    inter = sum(1 for x, y in zip(sa, sb) if x and y)
    union = sum(1 for x, y in zip(sa, sb) if x or y)
    same = sum(1 for x, y in zip(pa, pb) if x[:3] == y[:3]) / len(pa)
    return (inter / union if union else 0), same


def identify():
    """Which figure each sheet's own player matches best."""
    found = {}
    for sheet, raw in SHEETS:
        band_figures = figures(sheet)
        poses = players(sheet)
        best = []
        for i, figure in enumerate(band_figures):
            iou, same = max((agreement(figure, pose) for pose in poses),
                            key=lambda t: sum(t))
            best.append((iou + same, i, iou, same))
        best.sort(reverse=True)
        total, i, iou, same = best[0]
        runner = best[1]
        found[raw] = i
        print(f'  {sheet:7} (raw world {raw}): figure {i}'
              f'  overlap {iou:.2f}, pixels {same:.2f}'
              f'   next best figure {runner[1]} at {runner[0]:.2f} against {total:.2f}')
    return found


def colours_are_per_sheet():
    """How much a figure changes between sheets, in shape and in colour."""
    bands = {}
    for name, path in EGA.items():
        bands[name] = band(ega_indices(path))

    base = bands['HUNTER']
    for name, other in bands.items():
        if name == 'HUNTER':
            continue
        drawn = sum(1 for b in other if b)
        if drawn < 100:
            print(f'  {name}: the band is empty - {drawn} non-zero pixels')
            continue
        colour = sum(1 for a, b in zip(base, other) if a != b)
        shape = sum(1 for a, b in zip(base, other) if bool(a) != bool(b))
        print(f'  HUNTER against {name}: colour differs on {100 * colour / len(base):.0f}%'
              f' of pixels, silhouette on {100 * shape / len(base):.0f}%')


def write():
    os.makedirs(OUT, exist_ok=True)
    scale = 4
    contact = Image.new('RGB',
                        (FIGURES * (FIGURE_W * scale + 6) + 6,
                         len(SHEETS) * (FIGURE_H * scale + 6) + 6),
                        (16, 16, 22))
    for row, (sheet, _) in enumerate(SHEETS):
        folder = os.path.join(OUT, sheet)
        os.makedirs(folder, exist_ok=True)
        for i, figure in enumerate(figures(sheet)):
            figure.save(os.path.join(folder, f'{i}.png'))
            contact.paste(figure.convert('RGB').resize(
                (FIGURE_W * scale, FIGURE_H * scale), Image.NEAREST),
                (6 + i * (FIGURE_W * scale + 6), 6 + row * (FIGURE_H * scale + 6)))
    contact.save(os.path.join(OUT, 'contact.png'))
    print(f'  {FIGURES * len(SHEETS)} figures and a contact sheet written to '
          f'{os.path.relpath(OUT, ROOT)}')


# What each figure is, and what says so. The five marked `matched` come out of
# identify(); the rest are read off the art and are inferred, not proven.
READING = [
    ('scuba', 'goggles over the eyes'),
    ('hunter', 'matched'),
    ('cook', 'matched'),
    ('cowboy', 'matched'),
    ('player', 'an American football helmet, face guard down'),
    ('music', 'a tailcoat'),
    ('space', 'matched'),
    ('miner', 'a lamp on the front of the cap'),
    ('luck', 'a gambler in a wide-brimmed hat'),
    ('body', 'matched'),
]


def readme():
    """The directory explains itself, the way sprites/README.md does."""
    lines = [
        '# The ten placeholder players',
        '',
        'Ten figures, 16 by 32, sitting at `y = 144` in every shipped sprite sheet '
        'with nothing in the game pointing at them. Five are the shipped worlds; the '
        'other five are the only 1988 artwork the never-released worlds have.',
        '',
        '**These are 1988 artwork and stay here.** The site publishes the redrawn set '
        'and not this one, the same rule the sprite directory follows.',
        '',
        'Built by `tools/placeholders.py`. The derivation, and what is proven against '
        'what is merely read off the art, is in [FORMATS.md](../../docs/FORMATS.md).',
        '',
        '![All ten, from all five sheets](contact.png)',
        '',
        'The rows above are ' + ', '.join(s.title() for s, _ in SHEETS) + '.',
        '',
        '| Figure | World | How it is known |',
        '|---|---|---|',
    ]
    for i, (world, how) in enumerate(READING):
        bold = f'**{world}**' if how == 'matched' else world
        lines.append(f'| {i} | {bold} | {how} |')
    lines += [
        '',
        'Bold is settled from the data: that sheet\'s own player matches that figure '
        'better than any other. The rest is a reading of the picture, and the pair at '
        '4 and 8 is the weakest of it - the miner and the sportsman could be the other '
        'way round.',
        '',
    ]
    open(os.path.join(OUT, 'README.md'), 'w').write('\n'.join(lines))


def main():
    print('Does anything in the executables point at the band?')
    no_table_points_at_them()

    print('\nWhich figure is each sheet\'s own world?')
    found = identify()

    print('\nIs a figure the same drawing in every sheet?')
    colours_are_per_sheet()

    print('\nWriting the figures')
    write()
    readme()

    expected = {0: 1, 2: 2, 4: 3, 6: 6, 8: 9}
    if found != expected:
        raise SystemExit(f'\nthe identification moved: {found} against {expected}')
    print('\nFive of ten settled from the data: '
          + ', '.join(f'figure {i} is raw world {raw}' for raw, i in sorted(found.items())))


if __name__ == '__main__':
    main()
