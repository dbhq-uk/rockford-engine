#!/usr/bin/env python3
"""Comparing two 16x16 tiles, shared by hires.py and dissolve.py.

    python3 tools/tilecmp.py        runs its own check
"""
import sys

from PIL import Image, ImageChops


def differing(a, b):
    """How many pixels differ between two same-size tiles, in any of the four
    channels. Not getbbox() on the difference: for an RGBA image that looks only at
    the alpha channel, which is 255 on both sides of any two opaque tiles, so it
    called every pair of opaque tiles identical."""
    diff = ImageChops.difference(a.convert('RGBA'), b.convert('RGBA'))
    worst = diff.getchannel(0)
    for band in range(1, 4):
        worst = ImageChops.lighter(worst, diff.getchannel(band))
    return worst.point(lambda v: 255 if v else 0).histogram()[255]


def similarity(a, b):
    """The share of a tile's pixels the two agree on, 0 to 1."""
    return 1 - differing(a, b) / (a.width * a.height)


if __name__ == '__main__':
    red = Image.new('RGBA', (16, 16), (255, 0, 0, 255))
    blue = Image.new('RGBA', (16, 16), (0, 0, 255, 255))
    assert differing(red, blue) == 256 and differing(red, red) == 0
    print('tilecmp: all good')
    sys.exit(0)
