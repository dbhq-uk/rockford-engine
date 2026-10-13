#!/usr/bin/env python3
"""A directory of every object in the game, drawn in all five worlds.

    python3 tools/spritedex.py

Writes sprites/<value>.png - the same object as each of the five worlds draws
it, side by side at three times size - and sprites/README.md, a table of them
grouped by what they are.

The descriptions are of the OBJECT, not the picture. Five worlds draw a boulder
five ways and it is the same boulder in all of them: the interesting thing about
a reverse engineered game is what a cell does, and that is the same whichever
sheet is loaded. Where a rule is worth citing, the handler's address is given.
"""
import json
import sys
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import worldlist  # noqa: E402
from usage import help_and_exit, wants_help  # noqa: E402

# Before anything is read or written. This regenerated sprites/ and the public site's
# pictures on `--help`, and CI only passed because the output happened to match what
# was committed.
if wants_help():
    help_and_exit(sys.modules[__name__])

ROOT = Path(__file__).resolve().parent.parent
GAME = ROOT / 'web/Rockford.Web/wwwroot/game'
OUT = ROOT / 'sprites'

TILE = 16
SHOW = 3        # the strip is drawn at this many times size

# VIP's own art is eight times the size, so its tiles are cut from the x8 atlas
# and shown at their own resolution rather than magnified.
VIP_SCALE = 8
VIP_SHOW = 96   # pixels a side in the VIP table

# The five 1988 worlds, from worlds.json (#107): the columns of the catalogue in
# sprites/ and the top strip on the public /sprites page.
WORLDS = worldlist.first_page()
# And the five the menu cannot reach, for the site's second strip. Shown apart, not as
# five more columns of the same picture: their art is Aleksi Sahala's reconstruction
# and VIP's is generated from his, so the page credits him where they appear.
HIDDEN = [w for w in worldlist.all_worlds() if w not in WORLDS]
NUMBER = {5: 'five', 6: 'six', 7: 'seven', 8: 'eight', 9: 'nine', 10: 'ten'}

# value, name, what it does. Grouped the way a player meets them.
CATALOGUE = [
    ('Rockford', [
        (0x10, 'Rockford', 'The player. Twenty sprites: four facings, two idle poses '
                           'and the frames for pushing and firing. Which one is drawn '
                           'is a draw from the game\'s own generator, which is why the '
                           'engine owns the choice - `0x12335`.'),
    ]),
    ('The ground', [
        (0x01, 'Dirt', 'Walked through and left behind as space. The only thing '
                       'Rockford can dig.'),
        (0x02, 'Steel', 'Indestructible. Survives both kinds of explosion, and every '
                        'cave is walled in it.'),
        (0x03, 'Wall', 'An ordinary wall. Stops movement, and an explosion takes it.'),
        (0x04, 'Pass-through wall', 'Authored `0C`. Rockford walks through it and '
                                    'nothing else can.'),
    ]),
    ('What you are there for', [
        (0x40, 'Treasure', 'The quota. Collecting it counts down `ds:0x164A`, and the '
                           'exit opens on the first scan where that reaches zero.'),
        (0x42, 'Falling treasure', 'The same, mid-fall. Like a boulder it kills Rockford '
                                   'or a creature it lands on, but its blast leaves '
                                   'treasure where a boulder\'s leaves space (`0x14271` '
                                   'against `0x1401E`). This said it killed nothing '
                                   'until 13 October 2026.'),
        (0x6C, 'Treasure bonus', 'Authored `78`. Four frames, and the numeral drawn '
                                 'on the tile is what collecting it pays: `0x11709` '
                                 'pays `al - 0x6C`, so the frame showing 0 pays nothing '
                                 'and the frame showing 3 pays three. The sprite states '
                                 'its own value, which is why the redraw has to get the '
                                 'digit right.'),
        (0x70, 'Time bonus', 'Authored `7C`. Twenty seconds flat on any of its four '
                             'frames, at `0x100D2`. Not random, unlike its neighbour.'),
        (0x07, 'Closed exit', 'Opens on the first scan where the quota is exactly zero, '
                              'and never closes again. `0x14316`.'),
        (0x30, 'Open exit', 'Four frames, one every other tick. All four are walkable '
                            'and all four finish the cave. `0x14301`.'),
    ]),
    ('Things that fall', [
        (0x44, 'Boulder', 'Falls when the cell below is space, rolls off another '
                          'boulder, and kills what it lands on. Pushable sideways, but '
                          'only on a draw under `0x9E` - 158 in 256, at `0x1023D`.'),
        (0x46, 'Falling boulder', 'Mid-fall, and lethal. This is the state that kills.'),
    ]),
    ('Things that hunt', [
        (0x8C, 'Enemy, clockwise', 'Prefers to turn clockwise. Explodes into TREASURE, '
                                   'which makes it worth killing. `0x11B54`.'),
        (0x90, 'Enemy, anticlockwise', 'Turns the other way and explodes into space, so '
                                       'it is worth nothing. `0x11B91`. It is also what '
                                       'the evil worm turns a boulder into.'),
        (0x48, 'Amoeba', 'Grows into space and dirt on a per-neighbour draw. Sealed in, '
                         'the whole colony turns to treasure; left too long it turns to '
                         'boulders. `0x13E50`.'),
    ]),
    ('The worms', [
        (0x78, 'Evil worm, dormant', 'Authored `70`. Twenty seconds of four-frame '
                                     'animation, then it hatches - and touching it or '
                                     'shooting it writes those twenty seconds outright, '
                                     'so that is the fast way to a worm rather than a '
                                     'way to stop one. `0x13ED8`.'),
        (0xA0, 'Evil worm', 'Crawls a body of up to eight segments, turning treasure '
                            'back into boulders and boulders into the anticlockwise '
                            'enemy - the latter one time in eight. Boxed in, it turns '
                            'round and retraces itself. `0x1194B`.'),
        (0x7C, 'Good worm, dormant', 'Authored `84`. The same countdown. `0x14613`.'),
        (0xB0, 'Good worm', 'The same crawl and the opposite transform: the '
                            'anticlockwise enemy back to a boulder, and a boulder on to '
                            'treasure. One spoils a cave in the direction the other '
                            'mends it. `0x11A68`.'),
    ]),
    ('Walls that do something', [
        (0x15, 'Expanding wall', 'Authored `08`. Grows sideways into space, right '
                                 'before left, with a one-tick pause on each side '
                                 'before it can. `0x14632`.'),
        (0x74, 'Timed wall', 'Authored `04`. Something falling into it converts rather '
                             'than lands, for sixty seconds from the first time it '
                             'happens - and the window reopens if a cave runs past '
                             'nine minutes, because the comparison is signed. '
                             '`0x1467F`.'),
    ]),
    ('The taps and what they emit', [
        (0x08, 'Acid tap', 'Authored `C4`. Armed by being touched or shot, then drips '
                           'acid downward. `0x14445`.'),
        (0x0C, 'Fire tap', 'Authored `38`. The same, upward. `0x140AE`.'),
        (0x20, 'Acid', 'Falls. Meeting fire, both explode.'),
        (0x2C, 'Fire', 'Rises. Treasure dropped onto it becomes the treasure-yielding '
                       'enemy.'),
    ]),
    ('Explosions', [
        (0x11, 'Blast to treasure', 'The three-by-three a clockwise enemy leaves. Four '
                                    'frames, then treasure. Spares steel and the two '
                                    'producers only.'),
        (0x1B, 'Blast to space', 'The other kind. Spares steel, the producers AND the '
                                 'exit - so the exit survives this one and not the '
                                 'other.'),
    ]),
]


def strip(value, tables, sheets, scale=1, show=TILE * SHOW, worlds=None):
    """One object as each of `worlds` draws it, side by side - the five 1988 worlds
    unless told otherwise."""
    worlds = worlds or WORLDS
    if value == 0x10:
        pos = tables['player'][0]
    elif value == 0x8C:
        pos = tables['enemyA'][0]
    elif value == 0x90:
        pos = tables['enemyB'][0]

    # THE TWO WORMS, which this could not resolve at all. 0xA0 is 160 and
    # `main` holds 128, so both fell past every branch above into `return None`
    # - and the catalogue has described them since it was written, so the
    # entries were in sprites/README.md with the pictures silently missing.
    #
    # Same fault as the atlas coverage check, in a different tool: four of the
    # six tables TileSet.cs declares. See NOT_DRAWN_YET's note in hires.py.
    elif 0xA0 <= value <= 0xA7:
        pos = tables['groupA'][0]
    elif 0xB0 <= value <= 0xB7:
        pos = tables['groupB'][0]

    elif value < len(tables['main']):
        pos = tables['main'][value]
    else:
        return None

    side = TILE * scale
    sx, sy = pos[0] * scale, pos[1] * scale
    out = Image.new('RGBA', (show * len(worlds), show), (0, 0, 0, 0))

    # Magnified with nearest for the original, so its pixels stay pixels, and
    # sampled down with Lanczos for VIP, whose art is bigger than the cell.
    how = Image.NEAREST if scale == 1 else Image.LANCZOS

    for i, name in enumerate(worlds):
        tile = sheets[name].crop((sx, sy, sx + side, sy + side))
        out.alpha_composite(tile.resize((show, show), how), (i * show, 0))

    return out


def main():
    vip = '--vip' in sys.argv
    scale = VIP_SCALE if vip else 1
    show = VIP_SHOW if vip else TILE * SHOW
    out_dir = (ROOT / 'site/media/sprites') if vip else OUT

    tables = json.loads((GAME / 'sprites.json').read_text())
    sheets = {n: Image.open(GAME / (f'{n}-x{VIP_SCALE}.png' if vip else f'{n}.png')).convert('RGBA')
              for n in WORLDS + (HIDDEN if vip else [])}

    out_dir.mkdir(parents=True, exist_ok=True)

    lines = [
        '# The sprites',
        '',
        f'Every object the game can draw, in all {NUMBER[len(WORLDS)]} worlds, **as it shipped in 1988 and '
        'as this project redrew it**.',
        '',
        f'The {NUMBER[len(WORLDS)]} columns in each picture are **' + '**, **'.join(w.title() for w in WORLDS)
        + f'** - the order the select screen shows them. One object, {NUMBER[len(WORLDS)]} pictures: the '
        'sheets are interchangeable and the rules are not, so a boulder behaves like a '
        'boulder whichever one is loaded.',
        '',
        'The descriptions are of the OBJECT rather than the picture, because that is the '
        'part this project had to work out. Addresses are the handler in the unpacked '
        'executable; see [FORMATS.md](../docs/FORMATS.md) and '
        '[PORT-PLAN.md](../docs/PORT-PLAN.md).',
        '',
        'The redrawn set is published at '
        '[rockford.dbhq.uk/sprites](https://rockford.dbhq.uk/sprites). The original\'s '
        'is not, and stays here.',
        '',
        'Built by `tools/spritedex.py`.',
        '',
    ]

    drawn = 0
    for heading, entries in CATALOGUE:
        lines += [f'## {heading}', '',
                  '| 1988 | Redrawn | Value | Object | What it does |',
                  '|---|---|---|---|---|']

        for value, name, what in entries:
            art = strip(value, tables, sheets, scale, show)
            if art is None:
                continue

            art.save(out_dir / f'{value:02x}.png', optimize=True)
            drawn += 1
            if vip:
                (out_dir / 'hidden').mkdir(exist_ok=True)
                strip(value, tables, sheets, scale, show, HIDDEN).save(
                    out_dir / 'hidden' / f'{value:02x}.png', optimize=True)

            was = f'{value:02x}.png'
            now = f'../site/media/sprites/{value:02x}.png'
            lines.append(f'| ![{name}, 1988]({was}) | ![{name}, redrawn]({now}) | '
                         f'`{value:02X}` | **{name}** | {what} |')

        lines.append('')

    if vip:
        # The VIP pass owns the art and the catalogue the site page reads; the
        # markdown is written by the pass that has the ORIGINAL art, because that
        # is the column that cannot be regenerated from anything else.
        rows = [{'value': f'{v:02X}', 'file': f'{v:02x}.png', 'name': n,
                 'what': w, 'group': heading}
                for heading, entries in CATALOGUE for v, n, w in entries]
        (out_dir / 'catalogue.json').write_text(json.dumps(rows, indent=1))
    else:
        (out_dir / 'README.md').write_text('\n'.join(lines))

    print(f'{drawn} sprites in {out_dir.relative_to(ROOT)}')
    return 0


if __name__ == '__main__':
    sys.exit(main() or 0)
