#!/usr/bin/env python3
"""The EGA palettes, from the unpacked EGA executable.

Palettes are 17-byte blocks - 16 palette registers and the overscan byte - passed to
INT 10h, AX=1002h in ES:DX. Four utility blocks come first, then the ten worlds in
raw world order from 0x040A4, the five the 1988 menu reaches alternating with the
five it does not:

    0x04060  a second title palette, slightly different
    0x04071  all black, the fade
    0x04082  the default EGA set
    0x04093  the title screen
    0x040A4  hunter, then + 17 per raw world to miner at 0x0413D

Colour bytes are IRGB: bit 4 intensity, bits 2-0 red, green, blue.

    python3 tools/egapal.py                                  # list the palettes
    python3 tools/egapal.py HUNTER.FIL hunter out.png        # render with one

See docs/FORMATS.md for the derivation.
"""
import sys

STRIDE = 17
FIRST_WORLD = 0x040A4
UTILITY = {'title2': 0x04060, 'black': 0x04071, 'default': 0x04082,
           'title': 0x04093}
WORLDS = ['hunter', 'scuba', 'cook', 'player', 'cowboy',
          'music', 'space', 'luck', 'body', 'miner']


def palettes(image='unpacked/ega.bin'):
    """Return {name: [16 colour bytes]}, utility blocks first then the worlds."""
    d = open(image, 'rb').read()
    out = {}
    entries = list(UTILITY.items())
    entries += [(n, FIRST_WORLD + i * STRIDE) for i, n in enumerate(WORLDS)]
    for name, off in entries:
        entry = d[off:off + STRIDE]
        assert entry[16] == 0, f'{name}: expected the overscan 00 at {off + 16:#x}'
        out[name] = list(entry[:16])
    return out


def irgb(v):
    """One EGA colour byte to 8-bit RGB."""
    intense = (v >> 4) & 1
    if not intense and v == 6:
        return (170, 85, 0)                      # the brown fix
    hi, lo = (255, 85) if intense else (170, 0)
    return (hi if (v >> 2) & 1 else lo,
            hi if (v >> 1) & 1 else lo,
            hi if v & 1 else lo)


def rgb(world, image='unpacked/ega.bin'):
    return [irgb(b) for b in palettes(image)[world]]


if __name__ == '__main__':
    if len(sys.argv) == 1:
        for name, p in palettes().items():
            print(f"{name:7s} " + ' '.join(f'{b:02x}' for b in p))
    else:
        from PIL import Image
        sys.path.insert(0, 'tools')
        from ega import planar
        from usage import argv
        src, world, out = argv(3, 'egapal.py <input.fil> <world> <output.png>')
        im = Image.new('P', (320, 200))
        im.putdata(bytes(planar(open(src, 'rb').read(), (0, 1, 2, 3))))
        flat = [c for colour in rgb(world) for c in colour]
        im.putpalette(flat + [0] * (768 - len(flat)))
        im.convert('RGB').resize((640, 400), Image.NEAREST).save(out)
        print(out)
