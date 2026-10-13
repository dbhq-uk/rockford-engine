#!/usr/bin/env python3
"""Every playable cave drawn whole, and a page per world to show them.

    python3 tools/cavemaps.py

Writes levels/<world>-<stage>.png - the full 40 by 22 grid at its native 16
pixels a tile, so 640 by 352 - and levels/<world>.md around them.

The game never shows a cave like this. Its window is 20 by 11, half the width
and half the height, and it scrolls; seeing the whole thing at once is something
only the loader's output allows. That is the point of these: they are the cave
as authored, before anything has fallen, exploded or been collected.

Nothing here is a screenshot. Each map is built the way CaveLoader builds one -
the authored byte becomes a runtime value, the runtime value indexes the sprite
table read out of the executable, and the tile is cut from the world's own
sheet. So a map is exactly what the first frame of that cave looks like.
"""
import json
import os
import sys
from pathlib import Path

from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from usage import help_and_exit, wants_help  # noqa: E402

# Before anything is read: without the game data (the public repository has none)
# this crashed on `--help` instead of describing itself.
if wants_help():
    help_and_exit(sys.modules[__name__])

ROOT = Path(__file__).resolve().parent.parent
GAME = ROOT / 'web/Rockford.Web/wwwroot/game'
OUT = ROOT / 'levels'

WIDTH, HEIGHT = 40, 22
TILE = 16

# VIP draws the same caves from its own art, which is eight times the size. A
# whole cave at that scale is 5120 by 2816, so the finished map is resized down -
# once, at the end, rather than per tile, which keeps the sampling honest.
VIP_SCALE = 8
VIP_WIDTH = 1600

# The five the menu can reach, and the RAW world number each maps to. The cave
# record is raw * 4 + stage, so these are records 0-3, 8-11, 16-19, 24-27, 32-35
# rather than the first twenty - the worlds in between are the ones the 1988
# menu cannot reach. Two of them, scuba and player, shipped in November 1987.
WORLDS = [
    ('hunter', 0, 'Hunter', 'GOLD'),
    ('cook', 2, 'Cook', 'APPLES'),
    ('cowboy', 4, 'Cowboy', 'COINS'),
    ('space', 6, 'Space', 'SUNS'),
    ('body', 8, 'Body', 'HEARTS'),
    # The five the 1988 menu cannot reach, drawn the same way. Their caves are in
    # the same file as everybody else's; their art is Aleksi Sahala's reconstruction
    # (scuba and player also shipped in the November 1987 release), and VIP's is
    # generated from his, so a page that shows these credits him.
    ('scuba', 1, 'Scuba', 'GEMS'),
    ('player', 3, 'Player', 'CUPS'),
    ('music', 5, 'Music', 'NOTES'),
    ('luck', 7, 'Luck', 'CLUBS'),
    ('miner', 9, 'Miner', 'JEWELS'),
]

# Authored byte to runtime value, from CaveLoader.Load at 0x10878.
AUTHORED = {
    0x00: 0x00, 0x01: 0x01, 0x02: 0x02, 0x03: 0x03, 0x04: 0x74, 0x08: 0x15,
    0x0C: 0x04, 0x10: 0x48, 0x20: 0x10, 0x28: 0x44, 0x2C: 0x8C, 0x2D: 0x8C,
    0x2E: 0x8C, 0x30: 0x8C, 0x34: 0x90, 0x35: 0x90, 0x36: 0x90, 0x37: 0x90,
    0x38: 0x0C, 0x70: 0x78, 0x74: 0x07, 0x78: 0x6C, 0x7C: 0x70, 0x84: 0x7C,
    0x88: 0x40, 0xC4: 0x08,
}

# What each authored byte is, for the legend under a map. Only the ones a reader
# would want named; dirt and steel explain themselves.
NAMES = {
    0x20: 'Rockford', 0x74: 'the exit', 0x88: 'treasure', 0x28: 'a boulder',
    0x10: 'amoeba', 0x04: 'a timed wall', 0x08: 'an expanding wall',
    0x0C: 'a pass-through wall', 0x38: 'a fire tap', 0xC4: 'an acid tap',
    0x70: 'the evil worm', 0x84: 'the good worm',
    0x78: 'a treasure bonus', 0x7C: 'a time bonus',
    0x2C: 'an enemy, clockwise', 0x2D: 'an enemy, clockwise',
    0x2E: 'an enemy, clockwise', 0x30: 'an enemy, clockwise',
    0x34: 'an enemy, anticlockwise', 0x35: 'an enemy, anticlockwise',
    0x36: 'an enemy, anticlockwise', 0x37: 'an enemy, anticlockwise',
}


def sprite_for(tables, value):
    """Where a runtime value's tile sits on the sheet."""
    if value == 0x10:
        return tables['player'][0]
    if value == 0x8C:
        return tables['enemyA'][0]
    if value == 0x90:
        return tables['enemyB'][0]
    # No groupA or groupB branch, deliberately. The worms are 0xA0-0xB7 and no
    # authored cave holds one - they hatch from the eggs at 0x78 and 0x7C, which
    # ARE authored and do have art here. A cave map draws what was authored, so
    # this cannot reach them. Checked 20 September 2026 rather than assumed:
    # nothing in cellmaps.bin is in that range.
    if value < len(tables['main']):
        return tables['main'][value]
    return None


def draw(record, sheet, tables, floor, scale=1):
    """One cave, whole, as it stands the moment it is loaded."""
    # Space is the world's floor colour, not black. The game takes it from
    # palette index 0 of that world's own sheet, which is why the five look
    # different behind the same dirt.
    side = TILE * scale
    out = Image.new('RGBA', (WIDTH * side, HEIGHT * side), floor)

    for y in range(HEIGHT):
        for x in range(WIDTH):
            authored = record[y * WIDTH + x]
            value = AUTHORED.get(authored)
            if value is None:
                continue

            pos = sprite_for(tables, value)
            if pos is None:
                continue

            sx, sy = pos[0] * scale, pos[1] * scale
            out.alpha_composite(sheet.crop((sx, sy, sx + side, sy + side)),
                                (x * side, y * side))

    return out


def contents(record):
    """What is in a cave, counted, for the note under its map."""
    counts = {}
    for b in record:
        if b in NAMES:
            counts[NAMES[b]] = counts.get(NAMES[b], 0) + 1
    return counts


def main():
    vip = '--vip' in sys.argv
    scale = VIP_SCALE if vip else 1
    out_dir = (ROOT / 'site/media/caves') if vip else OUT

    maps = (GAME / 'cellmaps.bin').read_bytes()
    tables = json.loads((GAME / 'sprites.json').read_text())
    caves = json.loads((GAME / 'caves.json').read_text())
    worlds = json.loads((GAME / 'worlds.json').read_text())

    out_dir.mkdir(parents=True, exist_ok=True)
    written = 0

    for name, raw, title, treasure in WORLDS:
        sheet = Image.open(GAME / (f'{name}-x{VIP_SCALE}.png' if vip else f'{name}.png')).convert('RGBA')
        hexcolour = next(w['floor'] for w in worlds if w['sheet'] == name).lstrip('#')
        floor = tuple(int(hexcolour[i:i + 2], 16) for i in (0, 2, 4)) + (255,)

        lines = [
            f'# {title}',
            '',
            f'The four caves of the **{title}** world, drawn whole - **as they '
            f'shipped in 1988 and as this project redrew them**. Its treasure is '
            f'called **{treasure}**.',
            '',
            'The game never shows a cave like this. Its window is 20 by 11 and it '
            'scrolls, so half the width and half the height are off screen at any '
            'moment. These are the caves as authored, before anything has fallen, '
            'exploded or been collected.',
            '',
            f'Records {raw * 4} to {raw * 4 + 3} of the forty in `CELLMAPS.BIN`. '
            'Built by `tools/cavemaps.py`.',
            '',
        ]

        for stage in range(4):
            n = raw * 4 + stage
            record = maps[n * 880:(n + 1) * 880]

            image = draw(record, sheet, tables, floor, scale)
            if vip:
                height = round(image.height * VIP_WIDTH / image.width)
                image = image.resize((VIP_WIDTH, height), Image.LANCZOS)

            target = out_dir / f'{name}-{stage + 1}.png'
            image.save(target, optimize=True)
            # The homepage shows each world's first cave at about 230 pixels wide, and
            # ten of the 1600 pixel maps came to 4.2 MB there. A 480 pixel thumbnail.
            if vip and stage == 0:
                thumbs = ROOT / 'site/media/worlds'
                thumbs.mkdir(parents=True, exist_ok=True)
                image.resize((480, round(image.height * 480 / image.width)), Image.LANCZOS) \
                    .convert('RGB').save(thumbs / f'{name}.webp', quality=85, method=6)
            written += 1

            needed = caves['required'][n]
            seconds = caves['time'][n]
            before = caves['scoreBefore'][n]
            after = caves['scoreAfter'][n]

            lines += [
                f'## Cave {stage + 1}',
                '',
                '**1988**',
                '',
                f'![{title} cave {stage + 1}, 1988]({name}-{stage + 1}.png)',
                '',
                '**Redrawn**',
                '',
                f'![{title} cave {stage + 1}, redrawn]'
                f'(../site/media/caves/{name}-{stage + 1}.png)',
                '',
                f'**{needed} {treasure}** to open the exit, in **{seconds} seconds**. '
                f'Each one scores {before} before the quota is met and {after} after it.',
                '',
            ]

            found = contents(record)
            if found:
                listed = ', '.join(f'{count} × {what}' for what, count in sorted(found.items()))
                lines += [f'It holds {listed}.', '']

        if vip:
            page = [l.replace(f']({name}-', f'](../../site/media/caves/{name}-')
                    if l.startswith('![') else l for l in lines]
            page[2] = (f'The four caves of the **{title}** world, drawn whole in '
                       f"**this project's own artwork**. Its treasure is called "
                       f'**{treasure}**.')
            (ROOT / 'levels/vip').mkdir(parents=True, exist_ok=True)
            (ROOT / f'levels/vip/{name}.md').write_text('\n'.join(page))
        else:
            (out_dir / f'{name}.md').write_text('\n'.join(lines))
        print(f'{name}: four caves -> {out_dir.relative_to(ROOT)}')

    index = [
        '# The caves',
        '',
        'All forty caves, the five worlds the 1988 menu offers and the five it cannot '
        'reach, drawn whole rather than through the game\'s own 20 by 11 window.',
        '',
    ]
    for name, raw, title, treasure in WORLDS:
        index.append(f'- [{title}]({name}.md) - records {raw * 4} to {raw * 4 + 3}, '
                     f'treasure called {treasure}')
    index.append('')
    index.append('Built by `tools/cavemaps.py` from `CELLMAPS.BIN` and the sprite '
                 'tables in the executable.')
    if vip:
        vindex = ['# The caves, in VIP artwork', '',
                  'All forty caves, drawn whole, in the sheets redrawn for the enhanced '
                  'build. The five hidden worlds are generated from Aleksi Sahala\'s '
                  'reconstructions.', '',
                  "The original's versions are one directory up. This set is published, "
                  'at [rockford.dbhq.uk/caves](https://rockford.dbhq.uk/caves); that one '
                  'is not.', '']
        for name, raw, title, treasure in WORLDS:
            vindex.append(f'- [{title}]({name}.md) - records {raw * 4} to {raw * 4 + 3}')
        (ROOT / 'levels/vip/README.md').write_text('\n'.join(vindex) + '\n')
    else:
        (out_dir / 'README.md').write_text('\n'.join(index) + '\n')

    print(f'{written} maps in {out_dir.relative_to(ROOT)}')
    return 0


if __name__ == '__main__':
    sys.exit(main() or 0)
