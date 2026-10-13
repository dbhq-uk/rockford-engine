#!/usr/bin/env python3
"""Report what is inside a VGA .SCM or .CCM file.

    python3 tools/scm.py extracted/vga/*.SCM

The format itself lives in scmformat.py, which webassets.py uses too. This file
had its own copy of the decoder and that copy ran to the end of the file rather
than stopping at 64,000 pixels - so every shipped .SCM came back 147 to 156 bytes
too long, with the palette decoded into the image. It did not look like a
failure, which is why it survived: it returned a plausible buffer that a caller
would treat as pixels.

The tail is what follows the pixels. A .SCM carries its own palette there, so it
runs to about 193 bytes; a .CCM borrows one and has only a terminator.
"""
import sys

from scmformat import ScmError, decode


def main(paths):
    if '--help' in paths or '-h' in paths:
        print(__doc__.strip())
        return 0

    if not paths:
        print(__doc__.strip())
        return 0

    bad = 0
    for path in paths:
        try:
            data = open(path, 'rb').read()
        except OSError as e:
            print(f'{path:34s} {e.strerror}')
            bad += 1
            continue

        try:
            px, pos = decode(data)
        except ScmError as e:
            print(f'{path:34s} packed={len(data):6d}  NOT A SCREEN: {e}')
            bad += 1
            continue

        print(f'{path:34s} packed={len(data):6d} pixels={len(px):6d} '
              f'tail={len(data) - pos:4d}  distinct={len(set(px)):3d}')

    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
