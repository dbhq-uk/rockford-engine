#!/usr/bin/env python3
"""VIP's cartoons: the original's WIN, LOSE and TIME, redrawn at eight times.

The faithful build plays the original's cartoons, six 48 by 48 frames each, three to
a world (#137). VIP has none of the original's files by design, so this redraws
them: the same pictures, the same poses, drawn smoother and richer, never copied.
Everything in docs/ROCKFORD-VIP.md, "Read the tile before you prompt it", applies:
every frame is read before it is prompted, the reading is the prompt's pose, and
the first frame of a cartoon is passed to the rest for style and never for pose.

    python3 tools/vipcartoons.py extract    # the 48 by 48 originals, as references
    python3 tools/vipcartoons.py contact    # each world's frames, magnified and numbered
    python3 tools/vipcartoons.py plan       # what is read, what is drawn, what it costs
    python3 tools/vipcartoons.py generate   # draw what is not drawn yet
    python3 tools/vipcartoons.py strips     # assemble <world>-cartoon-x8.png
    python3 tools/vipcartoons.py verify     # no frame is the original enlarged
    python3 tools/vipcartoons.py site       # site/media/cartoons/<world>.webp for /vip
    python3 tools/vipcartoons.py --selftest

A strip is six frames across and three rows down, WIN then LOSE then TIME, each frame
384 by 384, which is the original's 48 at eight to one: the same sheet as the faithful
build's, so the page plays either one the same way.
"""
import json
import os
import subprocess
import sys
import time
from pathlib import Path

from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))

ROOT = Path(__file__).resolve().parent.parent
GAME = ROOT / 'web/Rockford.Web/wwwroot/game'
WORK = ROOT / 'build/vipcartoons'
READINGS = ROOT / 'tools/cartoon-readings.json'

#: The five 1988 worlds, which are the ones that have a cartoon.
WORLDS = ['hunter', 'cook', 'cowboy', 'space', 'body']
ANIMATIONS = ['win', 'lose', 'time']      # the strip's rows, top to bottom
FRAMES = 6
SIZE = 48
SCALE = 8
OUT = SIZE * SCALE                        # 384


def frame_of(strip, anim, n):
    """One original frame, from the faithful build's 288 by 144 strip."""
    r = ANIMATIONS.index(anim)
    return strip.crop((n * SIZE, r * SIZE, (n + 1) * SIZE, (r + 1) * SIZE))


def key(world, anim, n):
    return f'{world}/{anim}-{n}'


def reference_path(world, anim, n):
    return WORK / world / f'{anim}-{n}.ref.png'


def drawn_path(world, anim, n):
    return WORK / world / f'{anim}-{n}.png'


def cmd_extract():
    for world in WORLDS:
        strip = Image.open(GAME / f'{world}-cartoon.png').convert('RGB')
        (WORK / world).mkdir(parents=True, exist_ok=True)
        for anim in ANIMATIONS:
            for n in range(FRAMES):
                # Nearest to 512 so the model sees pixels rather than a blur.
                frame_of(strip, anim, n).resize((512, 512), Image.NEAREST).save(
                    reference_path(world, anim, n))
        print(f'{world}: {len(ANIMATIONS) * FRAMES} references')


def cmd_contact():
    """Each world's frames magnified and numbered, six across, one sheet a cartoon."""
    for world in WORLDS:
        strip = Image.open(GAME / f'{world}-cartoon.png').convert('RGB')
        for anim in ANIMATIONS:
            m = 4
            sheet = Image.new('RGB', (FRAMES * (SIZE * m + 6), SIZE * m + 22), (30, 30, 30))
            d = ImageDraw.Draw(sheet)
            for n in range(FRAMES):
                x = n * (SIZE * m + 6)
                sheet.paste(frame_of(strip, anim, n).resize((SIZE * m, SIZE * m), Image.NEAREST), (x, 18))
                d.text((x + 4, 3), f'{world} {anim} {n}', fill=(255, 255, 255))
            (WORK / world).mkdir(parents=True, exist_ok=True)
            sheet.save(WORK / world / f'{anim}.contact.png')
        print(f'{world}: contact sheets written')



# Each world's frame, the one-pixel border the original puts round it, at eight to one.
# The border is a plain rectangle, so it is built here and never asked of the model.
BORDER = {'hunter': (255, 255, 255), 'cook': (0, 0, 0), 'cowboy': (255, 255, 255),
          'space': None, 'body': (0, 0, 0)}

# gpt-image-2, as hires.py: a world is drawn start to finish with one model.
MODEL = os.environ.get('ROCKFORD_MODEL', 'gpt-image-2')
PACE = 4
PERMANENT = ('credit_balance_exhausted', 'insufficient_quota', 'no credits remaining',
             'billing_hard_limit_reached', 'invalid_api_key', 'incorrect api key')
REFUSED = ('moderation', 'safety system')
# The image CLI these were drawn with. IMAGER_DIR points at another install.
IMAGER_DIR = Path(os.environ.get('IMAGER_DIR', Path.home() / '.claude/skills/imager'))
IMAGER = IMAGER_DIR / 'scripts/imager.py'
IMAGER_PY = IMAGER_DIR / '.venv/bin/python'
COST = 0.024                       # what a draft sprite bills, from the imager's history

#: A redraw closer than this to the original enlarged is the original (mean 0-255 a channel).
COPY_LIMIT = 6.0


def readings():
    return json.loads(READINGS.read_text())


def reading_of(r, world, anim, n):
    """The words for one frame, or an error naming the frame that has none."""
    try:
        return r[world][anim]['frames'][n]
    except (KeyError, IndexError):
        raise SystemExit(f'no reading for {key(world, anim, n)}: read the frame '
                         f'(python3 tools/vipcartoons.py contact) and write it to '
                         f'tools/cartoon-readings.json first')


def anchor_of(world, anim, n):
    """The frame whose look this one follows, or None for the world's first frame.

    Style and character only, never pose. A cartoon's later frames follow its first;
    a cartoon's first follows the world's WIN first, so Rockford is one person in
    all three.
    """
    if n > 0:
        return (world, anim, 0)
    if anim != 'win':
        return (world, 'win', 0)
    return None


def prompt_for(r, world, anim, n, anchored):
    w = r[world]
    smooth = (
        'Draw it as a polished, smooth, high-resolution cartoon illustration with clean '
        'confident curved outlines, soft shading, rich colour and an expressive face. '
        'It must NOT look like pixel art: no visible pixels, no blocky or stair-stepped '
        'edges, no mosaic. The first image is a tiny 48 by 48 bitmap from a 1988 arcade '
        'game and you are drawing the artwork it was trying to be, better than it. '
    )
    ground = (f'The background is {w["background"]}, one flat solid colour filling the '
              'whole square to every edge, with no border, frame, text or signature. '
              'Keep the bitmap\'s colours. ')
    follow = (
        'The second image is another frame of the same cartoon. Use it for the drawing '
        'style and for how the characters look, and for nothing else. Do NOT copy its '
        'pose, props or layout: this frame\'s pose is the one described here. '
        if anchored else '')
    return (
        f'{smooth}'
        f'The subject is {w["subject"]}. The scene: {w[anim]["story"]}. '
        f'THIS FRAME, exactly as the bitmap shows it: {reading_of(r, world, anim, n)}. '
        f'Keep every prop, its count and its place exactly as described, and keep the '
        f'bitmap\'s composition: same positions, same facing, same size relative to the '
        f'square. {ground}{follow}Fill the square. The picture is square.')


def edit_base(r, world, anim, n):
    """The frame this one is an edit of, or None when it is drawn from its reference.

    A scene that must not move - the cowboy's grave, with only the ghost changing - is
    drawn once and the other frames are edits of it. Prompting each frame separately
    let the tombstone change size and place from one frame to the next.
    """
    base = r[world][anim].get('edit_of')
    return None if base is None or n == base else (world, anim, base)


def edit_prompt(r, world, anim, n):
    return (
        'Edit this picture and change only the ghost. Keep the sign, the tombstone, the '
        'mound and the background exactly as they are: the same place, the same size, the '
        'same colours and the same drawing style. The background stays one flat solid '
        f'colour. The ghost in this picture is: {reading_of(r, world, anim, n)}. Draw the '
        'ghost only in thin white outline, with no fill, and draw nothing else new.')


def order():
    """Every frame to draw, the anchors first: WIN 0, then LOSE 0 and TIME 0, then the rest."""
    def rank(item):
        _, anim, n = item
        return 0 if (anim == 'win' and n == 0) else 1 if n == 0 else 2
    items = [(w, a, n) for w in WORLDS for a in ANIMATIONS for n in range(FRAMES)]
    return sorted(items, key=rank)          # stable, so the worlds keep their order


def cmd_plan():
    r = readings()
    missing = []
    for world in WORLDS:
        for anim in ANIMATIONS:
            for n in range(FRAMES):
                try:
                    reading_of(r, world, anim, n)
                except SystemExit:
                    missing.append(key(world, anim, n))
    todo = [i for i in order() if not drawn_path(*i[:1], i[1], i[2]).exists()]
    total = len(WORLDS) * len(ANIMATIONS) * FRAMES
    print(f'{total} frames across {len(WORLDS)} worlds, {total - len(missing)} read, '
          f'{len(missing)} without a reading')
    print(f'{total - len(todo)} drawn, {len(todo)} to go')
    print(f'roughly ${len(todo) * COST:.2f} at draft quality')
    for m in missing:
        print('  no reading:', m)
    return 1 if missing else 0


def first_line(output):
    for line in output.splitlines():
        line = line.strip()
        if any(p in line for p in PERMANENT):
            return line[:160]
    return output.strip()[:160]


def cmd_generate(limit=None):
    r = readings()
    todo = [i for i in order() if not drawn_path(*i).exists()]
    if limit:
        todo = todo[:int(limit)]

    for i, item in enumerate(todo, 1):
        world, anim, n = item
        src, out = reference_path(*item), drawn_path(*item)
        if not src.exists():
            print(f'  no reference for {key(*item)} - run extract first')
            continue
        out.parent.mkdir(parents=True, exist_ok=True)

        base = edit_base(r, world, anim, n)
        if base:
            if not drawn_path(*base).exists():
                print(f'  {key(*item)} edits {key(*base)}, which is not drawn yet')
                continue
            args = [str(IMAGER_PY), str(IMAGER), '--draft', '--model', MODEL,
                    '--edit', str(drawn_path(*base)), '--background', 'opaque',
                    '--output-format', 'png', '--size', '1024x1024',
                    edit_prompt(r, world, anim, n), str(out)]
        else:
            a = anchor_of(*item)
            anchor_file = drawn_path(*a) if a and drawn_path(*a).exists() else None
            args = [str(IMAGER_PY), str(IMAGER), '--draft', '--model', MODEL,
                    '--background', 'opaque', '--output-format', 'png', '--size', '1024x1024',
                    '--reference', str(src)]
            if anchor_file:
                args += ['--reference', str(anchor_file)]
            args += [prompt_for(r, world, anim, n, bool(anchor_file)), str(out)]

        for attempt in range(1, 5):
            result = subprocess.run(args, capture_output=True, text=True)
            if out.exists():
                break
            output = (result.stdout + result.stderr).lower()
            if any(p in output for p in PERMANENT):
                print(f'\nStopping: {first_line(output)}', flush=True)
                print(f'{i - 1} of {len(todo)} done. Re-run generate to continue.', flush=True)
                return
            if any(p in output for p in REFUSED):
                break
            if attempt < 4:
                wait = PACE * (4 ** attempt) if 'rate' in output else 5
                print(f'[{i}/{len(todo)}] {key(*item)} retry {attempt} in {wait}s')
                time.sleep(wait)

        state = 'ok' if out.exists() else 'FAILED'
        print(f'[{i}/{len(todo)}] {key(*item)} {state}', flush=True)
        if state == 'FAILED':
            print('   ', (result.stderr or result.stdout).strip()[:200], flush=True)
        time.sleep(PACE)


# The flat colour behind each world's cartoons, which is the original's own palette entry
# for it. The model draws a background that is nearly flat, and a few levels of drift
# from one frame to the next pulses when six frames play in turn.
GROUND = {'hunter': (69, 53, 85), 'cook': (85, 85, 85), 'cowboy': (53, 85, 101),
          'space': (0, 69, 117), 'body': (101, 0, 0)}


def flatten(im, ground):
    """Make a frame's background exactly `ground`, and leave the drawing alone.

    The background is what is joined to the frame's edge and within reach of the colour
    in its corners. A pixel right on that colour becomes the flat colour; one further
    out keeps its own difference from it, fading to none by the time it is clearly
    drawing, so a glow or a soft shadow has no hard edge cut into it.
    """
    import numpy as np
    a = np.asarray(im.convert('RGB'), dtype=float)
    h, w, _ = a.shape
    k = 10
    corners = np.concatenate([a[:k, :k].reshape(-1, 3), a[:k, -k:].reshape(-1, 3),
                              a[-k:, :k].reshape(-1, 3), a[-k:, -k:].reshape(-1, 3)])
    est = np.median(corners, axis=0)
    d = abs(a - est).max(axis=2)

    reach = d <= 60
    seed = np.zeros((h, w), bool)
    seed[0, :] = seed[-1, :] = seed[:, 0] = seed[:, -1] = True
    seed &= reach
    while True:
        grown = seed.copy()
        grown[1:, :] |= seed[:-1, :]
        grown[:-1, :] |= seed[1:, :]
        grown[:, 1:] |= seed[:, :-1]
        grown[:, :-1] |= seed[:, 1:]
        grown &= reach
        if (grown == seed).all():
            break
        seed = grown

    target = np.array(ground, dtype=float)
    shift = target - est
    keep = np.clip((d - 8) / 52, 0, 1)[..., None]      # 0 at the colour, 1 well away
    out = a + shift * (1 - keep)                       # the correction fades with distance
    flat = np.clip(1 - d / 8, 0, 1)[..., None]         # and a pixel on the colour is snapped
    out = out * (1 - flat) + target * flat
    out = np.where(seed[..., None], out, a)
    return Image.fromarray(np.clip(out.round(), 0, 255).astype('uint8'))


def finished_frame(world, drawn):
    """One drawn frame at 384 by 384, its background flat, with the original's border round it."""
    im = flatten(drawn.convert('RGB').resize((OUT, OUT), Image.LANCZOS), GROUND[world])
    colour = BORDER[world]
    if colour:
        d = ImageDraw.Draw(im)
        d.rectangle((0, 0, OUT - 1, OUT - 1), outline=colour, width=SCALE)
    return im


def build_strip(world, frames):
    """frames[(anim, n)] -> the 2304 by 1152 strip."""
    strip = Image.new('RGB', (FRAMES * OUT, len(ANIMATIONS) * OUT))
    for (anim, n), im in frames.items():
        strip.paste(finished_frame(world, im), (n * OUT, ANIMATIONS.index(anim) * OUT))
    return strip


def strip_path(world):
    return GAME / f'{world}-cartoon-x{SCALE}.png'


def cmd_strips():
    for world in WORLDS:
        frames = {}
        for anim in ANIMATIONS:
            for n in range(FRAMES):
                p = drawn_path(world, anim, n)
                if not p.exists():
                    raise SystemExit(f'{key(world, anim, n)} is not drawn yet: run generate')
                frames[(anim, n)] = Image.open(p)
        build_strip(world, frames).save(strip_path(world), optimize=True)
        print(f'{world}: {strip_path(world).name}')


def difference(drawn, original):
    """Mean difference a channel between a redraw and the original enlarged, 0 to 255.

    Both are the picture inside the border, the redraw at any size and the original at
    its own, brought to one size first.
    """
    import numpy as np
    size = (OUT - 2 * SCALE, OUT - 2 * SCALE)
    a = np.asarray(drawn.convert('RGB').resize(size, Image.LANCZOS), dtype=float)
    b = np.asarray(original.convert('RGB').resize(size, Image.NEAREST), dtype=float)
    return float(abs(a - b).mean())


def inside(frame, border):
    return frame.crop((border, border, frame.width - border, frame.height - border))


def cmd_verify():
    """Fails if a strip is missing, the wrong size, or has a frame that is the original."""
    bad = 0
    for world in WORLDS:
        if not strip_path(world).exists():
            print(f'{world}: no {strip_path(world).name}')
            bad += 1
            continue
        mine = Image.open(strip_path(world)).convert('RGB')
        want = (FRAMES * OUT, len(ANIMATIONS) * OUT)
        if mine.size != want:
            print(f'{world}: strip is {mine.size}, not {want}')
            bad += 1
            continue
        original = Image.open(GAME / f'{world}-cartoon.png').convert('RGB')
        for anim in ANIMATIONS:
            row = ANIMATIONS.index(anim)
            for n in range(FRAMES):
                frame = mine.crop((n * OUT, row * OUT, (n + 1) * OUT, (row + 1) * OUT))
                d = difference(inside(frame, SCALE), inside(frame_of(original, anim, n), 1))
                if d < COPY_LIMIT:
                    print(f'{key(world, anim, n)}: the original enlarged (difference {d:.1f})')
                    bad += 1
    if bad:
        raise SystemExit(f'{bad} cartoon frames are missing or are the original\'s pixels')
    print(f'cartoons: {len(WORLDS) * len(ANIMATIONS) * FRAMES} frames, none the original enlarged')


def cmd_site():
    """The strips for the public site: 960 by 480, frames of 160, from the served strips."""
    out = ROOT / 'site/media/cartoons'
    out.mkdir(parents=True, exist_ok=True)
    for world in WORLDS:
        strip = Image.open(strip_path(world)).convert('RGB')
        strip.resize((FRAMES * 160, len(ANIMATIONS) * 160), Image.LANCZOS).save(
            out / f'{world}.webp', quality=88, method=6)
        print(f'{world}: site/media/cartoons/{world}.webp')


def cmd_selftest():
    failures = []

    def ok(name, got, want):
        if got != want:
            failures.append(f'{name}: got {got!r}, wanted {want!r}')

    r = readings()
    # Every one of the ninety frames has a reading, and says something.
    short = [key(w, a, n) for w in WORLDS for a in ANIMATIONS for n in range(FRAMES)
             if len(reading_of(r, w, a, n)) < 20]
    ok('every frame has a real reading', short, [])
    ok('every world has a background and a subject',
       [w for w in WORLDS if not (r[w]['background'] and r[w]['subject'])], [])
    ok('every animation has a story',
       [(w, a) for w in WORLDS for a in ANIMATIONS if not r[w][a]['story']], [])
    ok('six frames in each',
       [(w, a) for w in WORLDS for a in ANIMATIONS if len(r[w][a]['frames']) != FRAMES], [])

    # Order: WIN 0 of every world, then the other first frames, then the rest.
    o = order()
    ok('ninety frames', len(o), 90)
    ok('the anchors come first', [(a, n) for _, a, n in o[:5]], [('win', 0)] * 5)
    ok('then every other first frame', {n for _, _, n in o[5:15]}, {0})
    ok('and no later frame before its anchor',
       all(o.index(anchor_of(*i)) < o.index(i) for i in o if anchor_of(*i)), True)
    ok('WIN 0 follows nothing', anchor_of('hunter', 'win', 0), None)
    ok('LOSE 0 follows WIN 0', anchor_of('cook', 'lose', 0), ('cook', 'win', 0))
    ok('a later frame follows its first', anchor_of('cook', 'lose', 3), ('cook', 'lose', 0))

    # The prompt carries the reading, and says the anchor is for style alone.
    p = prompt_for(r, 'hunter', 'win', 0, False)
    ok('the reading is in the prompt', 'ONE black bat' in p, True)
    ok('no anchor, no mention of one', 'second image' in p.lower(), False)
    ok('an anchored prompt forbids its pose', 'Do NOT copy its pose' in prompt_for(r, 'hunter', 'win', 1, True), True)
    ok('the cowboy\'s grave is drawn once and edited', edit_base(r, 'cowboy', 'lose', 3), ('cowboy', 'lose', 0))
    ok('its base is not an edit of itself', edit_base(r, 'cowboy', 'lose', 0), None)
    ok('nothing else is an edit',
       [(w, a) for w in WORLDS for a in ANIMATIONS
        if (w, a) != ('cowboy', 'lose') and edit_base(r, w, a, 3)], [])
    ok('an edit says to change only the ghost', 'change only the ghost' in edit_prompt(r, 'cowboy', 'lose', 2), True)
    ok('a missing reading stops the run', _raises(lambda: reading_of(r, 'hunter', 'win', 9)), True)

    # A strip is built to size, with the border by code.
    def picture():
        im = Image.new('RGB', (1024, 1024), (10, 20, 30))
        ImageDraw.Draw(im).rectangle((300, 300, 700, 700), fill=(220, 40, 40))
        return im

    frames = {(a, n): picture() for a in ANIMATIONS for n in range(FRAMES)}
    strip = build_strip('hunter', frames)
    ok('strip size', strip.size, (2304, 1152))
    ok('hunter has a white border', strip.getpixel((0, 0)), (255, 255, 255))
    ok('its background is the flat colour', strip.getpixel((SCALE + 4, SCALE + 4)), (69, 53, 85))
    ok('and its picture is as drawn', strip.getpixel((OUT // 2, OUT // 2)), (220, 40, 40))
    ok('space has no border', build_strip('space', frames).getpixel((0, 0)), (0, 69, 117))

    # Flattening: a drifting background becomes the world's flat colour, and the drawing,
    # which is far from it, is left exactly as it was.
    drift = Image.new('RGB', (96, 96), (66, 52, 80))              # a shade off the flat colour
    ImageDraw.Draw(drift).rectangle((30, 30, 60, 60), fill=(220, 40, 40))
    flat = flatten(drift, (69, 53, 85))
    ok('the background is the flat colour', flat.getpixel((3, 3)), (69, 53, 85))
    ok('all of it', flat.getpixel((90, 90)), (69, 53, 85))
    ok('the drawing is left alone', flat.getpixel((45, 45)), (220, 40, 40))
    # A drawing the same colour as the background and shut off from the edge is not background.
    island = Image.new('RGB', (96, 96), (60, 50, 90))
    d = ImageDraw.Draw(island)
    d.rectangle((20, 20, 70, 70), fill=(250, 250, 250))
    d.rectangle((40, 40, 50, 50), fill=(60, 50, 90))
    ok('an enclosed patch of the colour is kept',
       flatten(island, (0, 0, 0)).getpixel((45, 45)), (60, 50, 90))

    # The copy check: the original enlarged is caught, a different picture is not.
    import random
    random.seed(7)
    orig = Image.new('RGB', (SIZE, SIZE))
    orig.putdata([(random.randrange(256), random.randrange(256), random.randrange(256))
                  for _ in range(SIZE * SIZE)])
    same = orig.resize((OUT, OUT), Image.NEAREST)
    other = Image.new('RGB', (OUT, OUT), (200, 40, 40))
    size = (OUT - 2 * SCALE, OUT - 2 * SCALE)
    ok('the original enlarged is caught',
       difference(inside(same, SCALE), inside(orig, 1)) < COPY_LIMIT, True)
    ok('a different picture is not',
       difference(inside(other, SCALE), inside(orig, 1)) < COPY_LIMIT, False)
    del size

    for f in failures:
        print('FAIL', f)
    print(f'vipcartoons selftest: {"FAILED" if failures else "ok"}')
    return 1 if failures else 0


def _raises(fn):
    try:
        fn()
    except SystemExit:
        return True
    return False


if __name__ == '__main__':
    action = sys.argv[1] if len(sys.argv) > 1 else 'plan'
    if action in ('-h', '--help', 'help'):
        print(__doc__)
        sys.exit(0)
    WORK.mkdir(parents=True, exist_ok=True)
    if action == 'extract':
        cmd_extract()
    elif action == 'contact':
        cmd_contact()
    elif action == 'plan':
        sys.exit(cmd_plan())
    elif action == 'generate':
        cmd_generate(sys.argv[2] if len(sys.argv) > 2 else None)
    elif action == 'strips':
        cmd_strips()
    elif action == 'verify':
        cmd_verify()
    elif action == 'site':
        cmd_site()
    elif action in ('selftest', '--selftest'):
        sys.exit(cmd_selftest())
    else:
        print(__doc__)
        sys.exit(2)
