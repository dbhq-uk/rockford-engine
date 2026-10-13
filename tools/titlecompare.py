#!/usr/bin/env python3
"""Every frame of the title animation, the original against VIP, side by side.

    python3 tools/titlecompare.py [out.png]

Composites from the same sheets and the same layout JSON the browser draws from,
and steps the counters the same way TitleAnimation.cs does - so a row here is
what the running build puts on the canvas for that frame, not an approximation of
it. The two builds are stacked per frame so the movement can be compared action
for action rather than by flicking between screenshots.

The counters come from 0xF29C:

  the toss   ping-pongs 0 to 8, holding each end an extra frame
  the arm    clamps to five frames, so it holds its reach while the gem rises
  the foot   runs on its own counter, turning at 2 and 1
"""
import json
import sys
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
GAME = ROOT / 'web/Rockford.Web/wwwroot/game'
VIP = ROOT / 'web/Rockford.Web/wwwroot/vip'

# The order 0xF2D0 calls them in, which decides what covers what.
LAYERS = ['arm', 'diamond', 'face', 'foot']

TOSS_FRAMES = 9
FOOT_FRAMES = 4
ARM_FRAMES = 5


def counters(steps):
    """The frame indices for each step, exactly as TitleAnimation advances them."""
    toss, toss_step = 0, 1
    foot, foot_step = 0, 1
    out = []

    for _ in range(steps):
        out.append((toss, foot))

        nxt = toss + toss_step
        if nxt >= TOSS_FRAMES:
            toss, toss_step = TOSS_FRAMES - 1, -1
        elif nxt < 0:
            toss, toss_step = 0, 1
        else:
            toss = nxt

        nxt = foot + foot_step
        if nxt >= FOOT_FRAMES:
            foot, foot_step = 2, -1
        elif nxt < 0:
            foot, foot_step = 1, 1
        else:
            foot = nxt

    return out


RELEASE = 3      # game.js TITLE_RELEASE: the gem is in his hand until here


def ease_in_out(t): return t * t * (3 - 2 * t)
def ease_in(t): return t * t
def ease_out(t): return 1 - (1 - t) * (1 - t)
def mix(a, b, t): return a + (b - a) * t
def span(v, lo, hi): return max(0.0, min(1.0, (v - lo) / (hi - lo)))


def arm_angle(c, rising):
    """game.js armAngle, line for line."""
    r = RELEASE
    if rising:
        if c <= 1: return mix(0, 5, ease_in_out(span(c, 0, 1)))
        if c <= r: return mix(5, -16, ease_in(span(c, 1, r)))
        if c <= r + 2: return mix(-16, 0, ease_in_out(span(c, r, r + 2)))
        return 0
    if c >= r + 1.5: return 0
    if c >= r: return mix(0, -7, ease_in_out(span(r + 1.5 - c, 0, 1.5)))
    if c >= r / 2: return mix(-7, 6, ease_out(span(r - c, 0, r / 2)))
    return mix(6, 0, ease_in_out(span(r / 2 - c, 0, r / 2)))


def turn(point, pivot, degrees):
    import math
    t = math.radians(degrees)
    (x, y), (px, py) = point, pivot
    return (px + (x - px) * math.cos(t) - (y - py) * math.sin(t),
            py + (x - px) * math.sin(t) + (y - py) * math.cos(t))


def gem_at(layout, c, rising):
    """game.js gemAt, line for line."""
    arc = layout['gem']['arc']
    last = len(arc) - 1
    arm = layout['arm']

    def in_hand(count):
        return turn(arm['palm'], arm['pivot'], arm_angle(count, rising))

    if c <= RELEASE:
        return in_hand(c)
    u = (c - RELEASE) / (last - RELEASE)
    h = 1 - (1 - u) * (1 - u)
    (x0, y0), (x1, y1) = in_hand(RELEASE), arc[last]
    return (x0 + (x1 - x0) * u, y0 + (y1 - y0) * h)


def paste_part(frame, sheet, part, degrees, lift=0):
    """A rig part turned about its pivot, the way game.js drawPart draws it."""
    sx, sy, sw, sh = part['src']
    img = sheet.crop((sx, sy, sx + sw, sy + sh))
    layer = Image.new('RGBA', frame.size, (0, 0, 0, 0))
    layer.alpha_composite(img, tuple(part['at']))
    px, py = part['pivot']
    # PIL turns anticlockwise for a positive angle; the canvas clockwise.
    layer = layer.rotate(-degrees, resample=Image.BICUBIC, center=(px, py),
                         translate=(0, lift))
    frame.alpha_composite(layer)


def compose_smooth(base, sheet, layout, toss, foot, rising=True):
    """VIP's title at a whole count, drawn the way game.js paintTitleSmooth
    draws it - without the gem's turn and glint, which are timed from the
    display rather than read off the counters, and without a blink, which is an
    event. Between whole counts the browser interpolates; this shows the counts."""
    frame = base.copy()

    for plate in layout['plates']:
        px, py, pw, ph = plate['src']
        frame.alpha_composite(sheet.crop((px, py, px + pw, py + ph)), tuple(plate['at']))

    shoe = layout['foot']
    paste_part(frame, sheet, shoe, shoe['counts'][min(foot, len(shoe['counts']) - 1)])

    arc = layout['gem']['arc']
    gx, gy = gem_at(layout, toss, rising)
    look = max(0.0, min(1.0, (arc[0][1] - gy) / (arc[0][1] - arc[-1][1])))

    paste_part(frame, sheet, layout['arm'], arm_angle(toss, rising))

    # The head and the eyes on it, turned together.
    tilt, lift = -2.4 * look, -2.5 * look
    head = Image.new('RGBA', frame.size, (0, 0, 0, 0))
    sx, sy, sw, sh = layout['head']['src']
    head.alpha_composite(sheet.crop((sx, sy, sx + sw, sy + sh)), tuple(layout['head']['at']))
    eyes = layout['eyes']
    ex, ey = eyes['frames'][round(look * (len(eyes['frames']) - 1))]
    head.alpha_composite(sheet.crop((ex, ey, ex + eyes['w'], ey + eyes['h'])), tuple(eyes['at']))
    hx, hy = layout['head']['pivot']
    frame.alpha_composite(head.rotate(-tilt, resample=Image.BICUBIC, center=(hx, hy), translate=(0, lift)))

    sx, sy, sw, sh = layout['gem']['src']
    frame.alpha_composite(sheet.crop((sx, sy, sx + sw, sy + sh)),
                          (round(gx - sw / 2), round(gy - sh / 2)))
    return frame


def compose(base, sheet, layout, toss, foot, alternate=False, rising=True):
    """One frame, drawn the way game.js draws it."""
    frame = base.copy()
    if sheet is None:
        return frame
    if layout.get('smooth'):
        return compose_smooth(base, sheet, layout, toss, foot, rising)

    for name in LAYERS:
        layer = layout.get('faceAlt' if (name == 'face' and alternate) else name)
        if layer is None:
            continue

        index = foot if name == 'foot' else toss
        index = min(index, len(layer['frames']) - 1)
        sx, sy = layer['frames'][index]
        w, h = layer['w'], layer['h']
        frame.alpha_composite(sheet.crop((sx, sy, sx + w, sy + h)), tuple(layer['at']))

    return frame


def build(which):
    """The base picture, its frame sheet and its layout, or None if it has none."""
    art = GAME if which == 'original' else VIP
    name = 'title.png' if which == 'original' else 'title.png'
    base = Image.open(art / name).convert('RGBA')

    sheet_path = art / 'titleani.png'
    layout_path = art / 'titleani.json'
    if not sheet_path.exists() or not layout_path.exists():
        return base, None, {}

    return base, Image.open(sheet_path).convert('RGBA'), json.loads(layout_path.read_text())


def main():
    from usage import wants_help, help_and_exit
    if wants_help():
        help_and_exit()

    out = Path(sys.argv[1] if len(sys.argv) > 1 else '/tmp/title-compare.png')

    orig = build('original')
    vip = build('vip')

    # One column per step of a full cycle, both builds stacked. The cycle is
    # eighteen steps, which is the toss out and back.
    steps = counters(18)
    cell_w = 300
    orig_h = round(cell_w * orig[0].height / orig[0].width)
    vip_h = round(cell_w * vip[0].height / vip[0].width)
    label = 22

    sheet = Image.new('RGB', (cell_w * len(steps), orig_h + vip_h + label * 2), (18, 16, 28))
    draw = ImageDraw.Draw(sheet)

    for i, (toss, foot) in enumerate(steps):
        x = i * cell_w
        draw.text((x + 6, 5), f'frame {i}  toss {toss}  foot {foot}', fill=(200, 200, 210))

        a = compose(*orig, toss, foot).convert('RGB').resize((cell_w, orig_h), Image.LANCZOS)
        sheet.paste(a, (x, label))

        draw.text((x + 6, label + orig_h + 4), 'VIP', fill=(200, 200, 210))
        b = compose(*vip, toss, foot, rising=(i < 9)).convert('RGB').resize((cell_w, vip_h), Image.LANCZOS)
        sheet.paste(b, (x, label * 2 + orig_h))

    sheet.save(out)
    print(f'{len(steps)} frames, both builds, in {out}')
    return 0


if __name__ == '__main__':
    sys.exit(main() or 0)
