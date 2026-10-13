#!/usr/bin/env python3
"""The taps' later frames, built from their finished first frame.

    python3 tools/tapturn.py         prints each tap's 1988 handle widths

Both taps animate one thing: the handle turning. The acid tap's is the bar across
its top, the fire tap's the bar along its bottom, and in every world the 1988
bar is 11, 9, 5 and 7 pixels wide across the four frames - side on, turning,
edge on, turning back. Nothing else moves.

Drawn frame by frame, the handle hardly changed width and the tap itself came
back a slightly different drawing each time, so it jumped once a cycle; a second
redraw did the same (#117). So frames 1 to 3 are the finished frame 0 with only
its handle band squeezed about its centre to that frame's 1988 width. The rest of
the tap is the same pixels in all four.
"""
import sys

from PIL import Image

TILE, SCALE = 16, 8
SIDE = TILE * SCALE

# row of the sheet -> which edge the handle is on
EDGE = {176: 'top', 64: 'bottom'}

# every later frame, and the first frame it is built from
POSITIONS = {(x, y): (256, y) for y in EDGE for x in (272, 288, 304)}

# The 1988 handle widths, frame 0 to 3, in every one of the five shipped worlds.
# A world applies only where ITS tiles measure the same: the hidden worlds' taps
# are not always taps - scuba's and luck's fire taps are birds, and miner's acid
# tap turns 11, 9, 5, 6 - so a world that does not match is drawn by the model
# rather than squeezed into something it is not (#110).
WIDTHS = [11, 9, 5, 7]

_measured = {}


def applies(world, pos):
    """Whether this world's tile at pos is built here: its row's handle measures
    the 1988 widths."""
    if pos not in POSITIONS:
        return False
    key = (world, pos[1])
    if key not in _measured:
        from pathlib import Path
        sheet = Image.open(Path(__file__).resolve().parent.parent
                           / f'web/Rockford.Web/wwwroot/game/{world}.png').convert('RGBA')
        got = [handle_width(sheet.crop((x, pos[1], x + TILE, pos[1] + TILE)), EDGE[pos[1]])
               for x in (256, 272, 288, 304)]
        _measured[key] = got == WIDTHS
    return _measured[key]


def handle_width(tile, where):
    """How wide the 1988 handle is: the widest row of the four at that edge."""
    rows = range(0, 4) if where == 'top' else range(12, 16)
    best = 0
    for y in rows:
        xs = [x for x in range(TILE) if tile.getpixel((x, y))[3]]
        if xs:
            best = max(best, max(xs) - min(xs) + 1)
    return best


def _span(tile, y):
    xs = [x for x in range(tile.width) if tile.getpixel((x, y))[3] > 40]
    return (min(xs), max(xs)) if xs else None


def band(vip, where):
    """The redraw's handle rows: from its edge, through the wide bar, to where
    the tap narrows to its stem."""
    rows = range(SIDE) if where == 'top' else range(SIDE - 1, -1, -1)
    spans = [(y, _span(vip, y)) for y in rows]
    spans = [(y, s) for y, s in spans if s]
    start = spans[0][0]
    widest = max(s[1] - s[0] for _, s in spans[:len(spans) // 3])
    in_bar = False
    for y, s in spans:
        width = s[1] - s[0]
        if width >= widest * .7:
            in_bar = True
        elif in_bar and width < widest * .45:
            return (start, y) if where == 'top' else (y + 1, start + 1)
    raise ValueError(f'no handle found at the {where} of the tap')


def frame(first, where, ratio):
    """`first` with its handle band squeezed horizontally by `ratio`."""
    y0, y1 = band(first, where)
    strip = first.crop((0, y0, SIDE, y1))
    spans = [s for s in (_span(strip, y) for y in range(strip.height)) if s]
    left, right = min(s[0] for s in spans), max(s[1] for s in spans) + 1
    width = max(2, round((right - left) * ratio))
    handle = strip.crop((left, 0, right, strip.height)).resize((width, strip.height), Image.LANCZOS)
    out = first.copy()
    out.paste((0, 0, 0, 0), (0, y0, SIDE, y1))
    out.alpha_composite(handle, (round((left + right) / 2 - width / 2), y0))
    return out


if __name__ == '__main__':
    if '--help' in sys.argv or '-h' in sys.argv:
        print(__doc__.strip())
        sys.exit(0)
    from pathlib import Path
    game = Path(__file__).resolve().parent.parent / 'web/Rockford.Web/wwwroot/game'
    for world in ('hunter', 'cook', 'cowboy', 'space', 'body'):
        sheet = Image.open(game / f'{world}.png').convert('RGBA')
        for y, where in EDGE.items():
            widths = [handle_width(sheet.crop((x, y, x + TILE, y + TILE)), where)
                      for x in (256, 272, 288, 304)]
            print(f'{world} {"acid" if y == 176 else "fire"} tap handle: {widths}')
