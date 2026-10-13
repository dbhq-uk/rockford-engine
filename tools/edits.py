#!/usr/bin/env python3
"""Fix a redrawn tile by working on the finished neighbour it belongs with.

    python3 tools/edits.py          lists the edits and which are done
    python3 tools/edits.py apply    applies the ones not yet done

A redraw with the same prompt makes the same mistake again (#117, #124), and the
mistakes left are almost all about how one frame differs from another: a card whose
hearts point the wrong way, a door a little further open, a foot a little narrower. So
those tiles are made FROM a finished tile - a copy, a turn, a flip, a shift, or an
imager edit that is told to change one thing and keep the rest - and the file written
is the tile's own out-<x>-<y>.png, so the atlas, the checks and the review page need
to know nothing about it.

tools/hidden-edits.json is {world: {"x,y": {"op": ..., "from": "x,y", ...}}}. `from` is
the tile it is made from (its own redraw, when omitted). Operations:

    copy          the source as it is
    rot180        turned half a turn
    rot90ccw      turned a quarter turn anticlockwise
    flip          mirrored left to right
    shift         moved by "dx", "dy" sixteenths of the tile, the gap left clear
    (any pure operation may also carry "recolor": {"from": [r,g,b], "to": [r,g,b], "tol": n})
    halfswap      top and bottom halves exchanged (a scrolling wall, half a step on)
    edit          the imager's edit, with "prompt"

An edit that has been applied is marked "done" in the file and left alone, so apply is safe to
rerun; set it back to false to redo one.
"""
import json
import os
import subprocess
import sys
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))

FILE = Path(__file__).resolve().parent / 'hidden-edits.json'
KEEP = (' Isolated on a transparent background, no shadow, no halo. Change nothing else: keep the '
        'same size, position, outline, shading and style.')


def entries():
    return json.loads(FILE.read_text()) if FILE.exists() else {}


def transform(op, image, spec):
    """The image after a pure operation, or None for an imager edit."""
    if op == 'copy':
        return image.copy()
    if op == 'rot180':
        return image.rotate(180)
    if op == 'rot90ccw':
        return image.rotate(90)
    if op == 'flip':
        return image.transpose(Image.FLIP_LEFT_RIGHT)
    if op == 'halfswap':
        w, h = image.size
        out = Image.new(image.mode, image.size)
        out.paste(image.crop((0, h // 2, w, h)), (0, 0))
        out.paste(image.crop((0, 0, w, h // 2)), (0, h // 2))
        return out
    if op == 'shift':
        w, h = image.size
        dx, dy = spec.get('dx', 0) * w // 16, spec.get('dy', 0) * h // 16
        out = Image.new(image.mode, image.size)
        out.paste(image, (dx, dy))
        return out
    return None


def recolor(image, spec):
    """Pixels near one colour changed to another, keeping their alpha. For the one-pixel
    details a flip or a swap carries over in the wrong shade."""
    src, dst, tol = spec['from'], spec['to'], spec.get('tol', 60)
    out = image.copy()
    px = out.load()
    for y in range(out.height):
        for x in range(out.width):
            r, g, b, a = px[x, y]
            if a and abs(r - src[0]) + abs(g - src[1]) + abs(b - src[2]) <= tol:
                px[x, y] = (dst[0], dst[1], dst[2], a)
    return out


def run(apply=False):
    import hires
    data = entries()
    todo = 0
    for world, tiles in data.items():
        for key, spec in tiles.items():
            x, y = (int(v) for v in key.split(','))
            target = hires.paths(world, x, y)[1]
            sx, sy = (int(v) for v in spec.get('from', key).split(','))
            # Through copies and flips: a source that is itself built has no file of its own.
            source = hires.paths(world, *hires.drawn_from(world, hires.mirrors(world).get((sx, sy), (sx, sy))))[1]
            done = bool(spec.get('done'))
            print(f'  {"done" if done else "todo":4} {world:7} {key:8} {spec["op"]:9} from {sx},{sy}')
            if done:
                continue
            todo += 1
            if not apply:
                continue
            image = Image.open(source).convert('RGBA')
            result = transform(spec['op'], image, spec)
            if result is None:
                imager_edit(source, spec['prompt'], target)
            else:
                if spec.get('recolor'):
                    result = recolor(result, spec['recolor'])
                result.save(target)
            # Recorded, because a turn is not safe to apply twice.
            spec['done'] = True
            FILE.write_text(json.dumps(data, indent=1, ensure_ascii=False))
            print(f'       wrote {target.name}')
    return todo


def imager_edit(source, prompt, target):
    import hires
    tmp = target.with_suffix('.edit.png')
    if tmp.exists():
        tmp.unlink()
    args = [str(hires.IMAGER_PY), str(hires.IMAGER), '--draft', '--model', hires.MODEL,
            '--edit', str(source), '--background', 'transparent', '--output-format', 'png',
            '--size', '1024x1024', prompt + KEEP, str(tmp)]
    result = subprocess.run(args, capture_output=True, text=True)
    if not tmp.exists():
        raise SystemExit(f'edit failed for {target.name}: {(result.stderr or result.stdout).strip()[-200:]}')
    tmp.replace(target)
    return Image.open(target).convert('RGBA')


def selftest():
    """The pure operations, on a tile with one marked pixel."""
    img = Image.new('RGBA', (16, 16), (0, 0, 0, 0))
    img.putpixel((2, 3), (255, 0, 0, 255))
    fails = []

    def ok(what, got, want):
        if got != want:
            fails.append(f'{what}: got {got}, wanted {want}')

    ok('rot180 moves (2,3) to (13,12)', transform('rot180', img, {}).getpixel((13, 12)), (255, 0, 0, 255))
    ok('flip moves (2,3) to (13,3)', transform('flip', img, {}).getpixel((13, 3)), (255, 0, 0, 255))
    ok('rot90ccw moves (2,3) to (3,13)', transform('rot90ccw', img, {}).getpixel((3, 13)), (255, 0, 0, 255))
    ok('shift by 2 down moves (2,3) to (2,5)', transform('shift', img, {'dy': 2}).getpixel((2, 5)), (255, 0, 0, 255))
    ok('shift leaves the gap clear', transform('shift', img, {'dy': 2}).getpixel((2, 3))[3], 0)
    ok('halfswap moves (2,3) to (2,11)', transform('halfswap', img, {}).getpixel((2, 11)), (255, 0, 0, 255))
    ok('an imager edit is not a pure operation', transform('edit', img, {}), None)
    for line in fails:
        print(f'  FAIL  {line}')
    print(f'edits: {"all good" if not fails else f"{len(fails)} failed"}')
    return 1 if fails else 0


if __name__ == '__main__':
    if '--selftest' in sys.argv:
        sys.exit(selftest())
    if '--help' in sys.argv or '-h' in sys.argv:
        print(__doc__.strip())
        sys.exit(0)
    os.environ.setdefault('ROCKFORD_MODEL', 'gpt-image-2.5-flare')
    run(apply='apply' in sys.argv)
