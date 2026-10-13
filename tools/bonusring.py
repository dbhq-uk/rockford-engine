#!/usr/bin/env python3
"""The two bonus tiles' border, drawn in code from the 1988 outline.

    python3 tools/bonusring.py        checks the outline against every bonus tile

The treasure bonus and the time bonus sit inside a one-pixel border shaped like a
square with a point pushed out from each side, and eight accent-coloured pixels on
it step one place clockwise each frame. That is the only thing that moves in the
border, and it is the thing the redraw could not hold: drawn frame by frame, the
ring came back a diamond in one frame and a pointed square in the next, with the
accents wherever the model put them. A second redraw changed nothing (#117).

So the border is not drawn by the model at all. hires.py atlas keeps the redrawn
CENTRE of each frame - the digit, or the clock, which the check of 30 September
2026 found right in every world - and draws the ring over it here: the 1988
outline as a shaded tube, and a bead wherever that frame's 1988 tile has an
accent. The same geometry in all five worlds and both bonuses, the colours taken
from each world's own tile.
"""
import math
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

sys.path.insert(0, str(Path(__file__).resolve().parent))
import worldlist  # noqa: E402

T, S = 16, 8
GAME = Path(__file__).resolve().parent.parent / 'web/Rockford.Web/wwwroot/game'
# The five 1988 worlds, which the outline was measured on and which check() holds to
# it. A hidden world is not held to it: fits() asks whether ITS tiles match, and the
# ring is drawn only where they do (scuba's, player's and music's are another shape).
WORLDS = worldlist.first_page()

# The sixteen tiles it applies to: four treasure bonus frames, four time bonus.
POSITIONS = {(x, 128) for x in range(0, 128, 16)}


def loop(tile):
    """The ring's pixels in order round the loop, and which are accents."""
    px = tile.load()
    solid = {(x, y) for y in range(T) for x in range(T) if px[x, y][3]}
    start = next(p for p in sorted(solid, key=lambda p: (p[1], p[0])) if p[1] == 0)
    comp, stack = set(), [start]
    while stack:
        p = stack.pop()
        if p in comp:
            continue
        comp.add(p)
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                q = (p[0] + dx, p[1] + dy)
                if q in solid and q not in comp:
                    stack.append(q)
    # order by angle round the centre, clockwise from the top
    order = sorted(comp, key=lambda p: (math.atan2(p[0] + .5 - 8, -(p[1] + .5 - 8)) % (2 * math.pi)))
    colours = {}
    for p in comp:
        colours[px[p][:3]] = colours.get(px[p][:3], 0) + 1
    body = max(colours, key=colours.get)
    accents = [p for p in comp if px[p][:3] != body]
    accent = px[accents[0]][:3] if accents else body
    return order, comp, accents, body, accent


def shade(c, k):
    return tuple(max(0, min(255, int(v * k))) for v in c)


# The 1988 outline, as the polygon its pixels trace: a square from 2 to 13 with a
# point pushed out from the middle of each side. Pixel centres, clockwise from
# the top point. The same in all five worlds and both bonuses - checked below.
OUTLINE = [(8, .5), (10.5, 2.5), (13.5, 2.5), (13.5, 5.5), (15.5, 8), (13.5, 10.5),
           (13.5, 13.5), (10.5, 13.5), (8, 15.5), (5.5, 13.5), (2.5, 13.5), (2.5, 10.5),
           (.5, 8), (2.5, 5.5), (2.5, 2.5), (5.5, 2.5)]


def near(p):
    """The point of OUTLINE nearest p, so a bead sits on the tube."""
    best, bd = None, 1e9
    n = len(OUTLINE)
    for i in range(n):
        (ax, ay), (bx, by) = OUTLINE[i], OUTLINE[(i + 1) % n]
        dx, dy = bx - ax, by - ay
        t = max(0, min(1, ((p[0] - ax) * dx + (p[1] - ay) * dy) / (dx * dx + dy * dy)))
        q = (ax + t * dx, ay + t * dy)
        d = (q[0] - p[0]) ** 2 + (q[1] - p[1]) ** 2
        if d < bd:
            best, bd = q, d
    return best, bd ** .5


def ring(order, accents, body, accent, up=4):
    side = T * S * up
    im = Image.new('RGBA', (side, side), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    u = S * up
    pts = [(x * u, y * u) for x, y in OUTLINE]
    pts.append(pts[0])
    d.line(pts, fill=shade(body, .35) + (255,), width=int(u * 1.35), joint='curve')
    d.line(pts, fill=body + (255,), width=int(u * .95), joint='curve')
    d.line([(x - u * .1, y - u * .12) for x, y in pts], fill=tuple(min(255, v + 90) for v in body) + (150,), width=int(u * .22), joint='curve')
    for a in accents:
        (bx, by), _ = near((a[0] + .5, a[1] + .5))
        cx, cy, r = bx * u, by * u, u * .8
        d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=shade(accent, .35) + (255,))
        r2 = r * .78
        d.ellipse((cx - r2, cy - r2, cx + r2, cy + r2), fill=accent + (255,))
        r3 = r * .28
        d.ellipse((cx - r2 * .45 - r3, cy - r2 * .45 - r3, cx - r2 * .45 + r3, cy - r2 * .45 + r3), fill=tuple(min(255, v + 110) for v in accent) + (220,))
    return im.resize((T * S, T * S), Image.LANCZOS)


def interior_mask(comp, clock=False):
    """The redrawn centre to keep: the clock face as a disc, the digit inside a
    shrunken copy of the outline - both clear of wherever the redraw put its ring."""
    side = T * S
    if clock:
        m = Image.new('L', (side, side), 0)
        r = 4.9 * S   # inside the outline's inner edge at 5.5, clear of the old ring's shadow
        ImageDraw.Draw(m).ellipse((side / 2 - r, side / 2 - r, side / 2 + r, side / 2 + r), fill=255)
        return m.filter(ImageFilter.GaussianBlur(1))
    m = Image.new('L', (side, side), 0)
    c = side / 2
    ImageDraw.Draw(m).polygon([(c + (x * S - c) * .66, c + (y * S - c) * .66) for x, y in OUTLINE], fill=255)
    return m.filter(ImageFilter.GaussianBlur(1))


def largest_piece(alpha, solid=24):
    """The alpha with everything but its largest connected piece cleared.

    The crop keeps the digit or the clock face, and sometimes a sliver of the
    redraw's own ring that reached inside it - cowboy's clocks kept red scraps
    under the face. Those are separate from the centre, so they are dropped.
    """
    w, h = alpha.size
    a = alpha.load()
    seen = [[False] * w for _ in range(h)]
    best = []
    for y0 in range(h):
        for x0 in range(w):
            if seen[y0][x0] or a[x0, y0] < solid:
                continue
            piece, stack = [], [(x0, y0)]
            seen[y0][x0] = True
            while stack:
                x, y = stack.pop()
                piece.append((x, y))
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1)):
                    nx, ny = x + dx, y + dy
                    if 0 <= nx < w and 0 <= ny < h and not seen[ny][nx] and a[nx, ny] >= solid:
                        seen[ny][nx] = True
                        stack.append((nx, ny))
            if len(piece) > len(best):
                best = piece
    keep = Image.new('L', alpha.size, 0)
    k = keep.load()
    for x, y in best:
        k[x, y] = 255
    # Grow it by a pixel so the soft edge of the piece survives the cut.
    keep = keep.filter(ImageFilter.MaxFilter(3))
    return Image.composite(alpha, Image.new('L', alpha.size, 0), keep)


def frame(original, vip, x):
    """The finished bonus tile: the redraw's centre, and the ring drawn here.

    `original` is the 16-pixel 1988 tile, `vip` the redrawn tile at 128.
    """
    order, comp, accents, body, accent = loop(original)
    keep = interior_mask(comp, clock=x >= 64)
    centre = vip.copy()
    centre.putalpha(largest_piece(
        Image.composite(vip.getchannel('A'), Image.new('L', vip.size, 0), keep)))
    out = Image.new('RGBA', vip.size, (0, 0, 0, 0))
    out.alpha_composite(centre)
    out.alpha_composite(ring(order, accents, body, accent))
    return out


_fits = {}


def fits(world):
    """Whether this world's bonus tiles sit on the 1988 outline with eight accents
    each. A world that does not keeps the model's own ring (#110): music's and
    luck's treasure bonus are not the same shape of border."""
    if world not in _fits:
        sheet = Image.open(GAME / f'{world}.png').convert('RGBA')
        try:
            worst = 0
            for x in range(0, 128, 16):
                order, comp, accents, body, accent = loop(sheet.crop((x, 128, x + T, 128 + T)))
                if len(accents) != 8:
                    raise ValueError
                worst = max([worst] + [near((p[0] + .5, p[1] + .5))[1] for p in comp])
            _fits[world] = worst <= .5
        except (ValueError, StopIteration):
            _fits[world] = False
    return _fits[world]


def check():
    """Every ring pixel of every bonus tile lies on OUTLINE, within half a pixel,
    and every tile has its eight accents. The distance, for the record."""
    worst = 0
    for w in WORLDS:
        sheet = Image.open(GAME / f'{w}.png').convert('RGBA')
        for x in range(0, 128, 16):
            order, comp, accents, body, accent = loop(sheet.crop((x, 128, x + T, 128 + T)))
            for p in comp:
                worst = max(worst, near((p[0] + .5, p[1] + .5))[1])
            assert len(accents) == 8, (w, x, len(accents))
    return worst


if __name__ == '__main__':
    if '--help' in sys.argv or '-h' in sys.argv:
        print(__doc__.strip())
        sys.exit(0)
    worst = check()
    print(f'bonus ring: every border pixel within {worst:.2f} of the outline, '
          'eight accents on every tile')
    sys.exit(0 if worst <= .5 else 1)
