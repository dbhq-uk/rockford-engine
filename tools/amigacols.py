#!/usr/bin/env python3
"""Read files off an Amiga OFS disk image (ADF) and decode Rockford's `cols`.

    python3 tools/amigacols.py ls   <disk.adf>
    python3 tools/amigacols.py cat  <disk.adf> <path> > out
    python3 tools/amigacols.py cols <disk.adf> [out.png]
    python3 tools/amigacols.py worlds <disk.adf> out.png

`cols` is 2560 bytes: 40 sets of 32 Amiga colours, one word each, 0RGB with four
bits a channel. Colour indexes 2, 4 and 5 carry the 0x8000 bit in some sets.
That bit is not a colour and is masked here; what it means is not known. Which
cave a set belongs to is inferred, not read from code: the sets run four to a
world, in the cave order of CELLMAPS.BIN. That is confirmed for the five worlds
that have art, whose `LEV/` sheets come out right with sets 0, 8, 16, 24 and 32,
and `sel_cols` holds exactly those five sets. It is extrapolated for the rest.

`worlds` renders each world's `ANI/` (left) and `LEV/` (right) file, which are
320 by 200 screens of five bitplanes, one plane after the other.
"""
import struct
import sys

B = 512
WORLDS = ["hunter", "scuba", "cook", "player", "cowboy",
          "music", "space", "luck", "body", "miner"]


class Adf:
    def __init__(self, path):
        self.d = open(path, "rb").read()
        if len(self.d) != 901120:
            raise SystemExit(f"{path}: not a 880K ADF ({len(self.d)} bytes)")

    def be(self, o):
        return struct.unpack(">I", self.d[o:o + 4])[0]

    def name(self, blk):
        o = blk * B
        n = self.d[o + B - 80]
        return self.d[o + B - 79:o + B - 79 + n].decode("latin1")

    def entries(self, blk, path=""):
        o = blk * B
        for i in range(72):
            b = self.be(o + 24 + 4 * i)
            while b:
                o2 = b * B
                sec = struct.unpack(">i", self.d[o2 + B - 4:o2 + B])[0]
                p = path + self.name(b)
                if sec == 2:
                    yield p + "/", b, None
                    yield from self.entries(b, p + "/")
                else:
                    yield p, b, self.be(o2 + 324)
                b = self.be(o2 + B - 16)

    def read(self, blk):
        o = blk * B
        size = self.be(o + 324)
        out = b""
        while True:
            for i in range(self.be(o + 8)):
                q = self.be(o + 24 + 4 * (71 - i)) * B
                out += self.d[q + 24:q + 24 + self.be(q + 12)]
            nxt = self.be(o + B - 8)
            if not nxt:
                return out[:size]
            o = nxt * B

    def file(self, path):
        for p, blk, size in self.entries(880):
            if p == path and size is not None:
                return self.read(blk)
        raise SystemExit(f"{path}: not on this disk")


def palettes(cols):
    words = struct.unpack(">%dH" % (len(cols) // 2), cols)
    return [[w & 0x0FFF for w in words[s * 32:(s + 1) * 32]]
            for s in range(len(words) // 32)]


def rgb(v):
    return tuple(((v >> sh) & 15) * 17 for sh in (8, 4, 0))


def render(sets, out):
    from PIL import Image, ImageDraw
    cw = ch = 22
    lab = 110
    img = Image.new("RGB", (lab + 32 * cw, len(sets) * ch), (255, 255, 255))
    dr = ImageDraw.Draw(img)
    for s, pal in enumerate(sets):
        dr.text((4, s * ch + 5), "%2d %s %d" % (s, WORLDS[s // 4], s % 4 + 1),
                fill=(0, 0, 0))
        for c, v in enumerate(pal):
            dr.rectangle([lab + c * cw, s * ch, lab + c * cw + cw - 2,
                          s * ch + ch - 2], fill=rgb(v))
    img.save(out)


def screen(data, pal):
    from PIL import Image
    bpr, h, n = 40, 200, 5
    img = Image.new("RGB", (320, h))
    px = img.load()
    for y in range(h):
        for xb in range(bpr):
            planes = [data[p * bpr * h + y * bpr + xb] for p in range(n)]
            for bit in range(8):
                i = sum(((planes[p] >> (7 - bit)) & 1) << p for p in range(n))
                px[xb * 8 + bit, y] = rgb(pal[i])
    return img


WORLD_FILES = [("hunt", 0), ("cook", 2), ("cowb", 4), ("astr", 6), ("doct", 8)]


def worlds(adf, out):
    from PIL import Image
    sets = palettes(adf.file("cols"))
    sheet = Image.new("RGB", (640, 200 * len(WORLD_FILES)))
    for i, (name, world) in enumerate(WORLD_FILES):
        for j, kind in enumerate(("ANI", "LEV")):
            sheet.paste(screen(adf.file(f"{kind}/{name}"), sets[world * 4]),
                        (j * 320, i * 200))
    sheet.save(out)


def main(argv):
    if len(argv) < 3:
        raise SystemExit(__doc__)
    cmd, adf = argv[1], Adf(argv[2])
    if cmd == "ls":
        for p, _, size in adf.entries(880):
            print(p if size is None else f"{p}  {size}")
    elif cmd == "cat":
        sys.stdout.buffer.write(adf.file(argv[3]))
    elif cmd == "cols":
        sets = palettes(adf.file("cols"))
        if len(argv) > 3:
            render(sets, argv[3])
        for s, pal in enumerate(sets):
            print("%2d %-7s %d  %s" % (s, WORLDS[s // 4], s % 4 + 1,
                                      " ".join("%03x" % v for v in pal)))
    elif cmd == "worlds":
        worlds(adf, argv[3])
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv)
