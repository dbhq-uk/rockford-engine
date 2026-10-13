#!/usr/bin/env python3
"""Decode a 32000-byte Rockford EGA image (.FIL / .CAR) to PNG.

32000 bytes = 320x200 at 4bpp. Two plausible encodings are tried:
  planar  - 4 sequential bitplanes of 8000 bytes, 1 bit per pixel each
  packed  - linear, 2 pixels per byte (high nibble first)
"""
import sys, os
from PIL import Image

EGA = [(0,0,0),(0,0,170),(0,170,0),(0,170,170),(170,0,0),(170,0,170),(170,85,0),
       (170,170,170),(85,85,85),(85,85,255),(85,255,85),(85,255,255),(255,85,85),
       (255,85,255),(255,255,85),(255,255,255)]

W, H = 320, 200

def planar(d, order=(0,1,2,3)):
    plane = 8000
    px = bytearray(W*H)
    for bit, p in enumerate(order):
        base = p*plane
        for i in range(plane):
            b = d[base+i]
            if not b: continue
            o = i*8
            for k in range(8):
                if b & (0x80 >> k):
                    px[o+k] |= (1 << bit)
    return px

def packed(d):
    px = bytearray(W*H)
    for i in range(W*H//2):
        b = d[i]
        px[i*2] = b >> 4
        px[i*2+1] = b & 15
    return px

def save(px, path, scale=2):
    im = Image.new('P', (W, H))
    im.putdata(bytes(px))
    pal = []
    for c in EGA: pal += list(c)
    im.putpalette(pal + [0]*(768-len(pal)))
    im = im.convert('RGB').resize((W*scale, H*scale), Image.NEAREST)
    im.save(path)
    return path

if __name__ == '__main__':
    from usage import argv
    src, out = argv(2, 'ega.py <input.fil> <output.png>')
    d = open(src, 'rb').read()
    os.makedirs(os.path.dirname(out) or '.', exist_ok=True)
    stem = out[:-4] if out.endswith('.png') else out
    save(planar(d), stem + '-planar.png')
    save(packed(d), stem + '-packed.png')
    print(stem + '-planar.png', stem + '-packed.png')
