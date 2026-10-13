#!/usr/bin/env python3
"""Read the Atari ST release's caves and compare them with the PC's CELLMAPS.BIN.

    python3 tools/stcaves.py ls     <disk.st>
    python3 tools/stcaves.py caves  <disk.st> [CELLMAPS.BIN]

The ST disk is a plain FAT12 disk: `AUTO/ROCK.PRG`, `AUTO/FORD.PRG` and
`DATA.D`. `DATA.D` is a 2360-byte loader stub and then the game's assets
(graphics in the ST's own 4-plane low-resolution format, then caves). The last
20 * 880 bytes of it are the game's 20 caves, in the same 40 by 22 byte-per-cell
format as CELLMAPS.BIN, so they can be compared cell for cell. They are not the
PC's order, and some are altered or new; `caves` prints each against its closest PC cave.
"""
import struct
import sys

WORLDS = ["hunter", "scuba", "cook", "player", "cowboy",
          "music", "space", "luck", "body", "miner"]
CAVE = 880


def name(i):
    return "%s %d" % (WORLDS[i // 4], i % 4 + 1)


class St:
    def __init__(self, path):
        d = self.d = open(path, "rb").read()
        self.spc = d[13]
        res = struct.unpack("<H", d[14:16])[0]
        nfat = d[16]
        self.ndir = struct.unpack("<H", d[17:19])[0]
        spf = struct.unpack("<H", d[22:24])[0]
        self.fat = d[res * 512:(res + spf) * 512]
        self.root = (res + nfat * spf) * 512
        self.data = self.root + self.ndir * 32

    def fatent(self, n):
        o = n * 3 // 2
        v = struct.unpack("<H", self.fat[o:o + 2])[0]
        return (v >> 4) if n & 1 else (v & 0xFFF)

    def read(self, n, size=None):
        out = b""
        while 2 <= n < 0xFF0:
            o = self.data + (n - 2) * self.spc * 512
            out += self.d[o:o + self.spc * 512]
            n = self.fatent(n)
        return out if size is None else out[:size]

    def walk(self, raw, path=""):
        for i in range(len(raw) // 32):
            e = raw[i * 32:i * 32 + 32]
            if e[0] == 0:
                break
            if e[0] in (0xE5, 0x2E) or e[11] == 0x0F:
                continue
            nm = e[:8].decode("latin1").rstrip()
            ex = e[8:11].decode("latin1").rstrip()
            full = nm + ("." + ex if ex else "")
            cl = struct.unpack("<H", e[26:28])[0]
            sz = struct.unpack("<I", e[28:32])[0]
            if e[11] & 0x10:
                yield path + full + "/", None
                yield from self.walk(self.read(cl), path + full + "/")
            else:
                yield path + full, self.read(cl, sz)

    def files(self):
        return dict(self.walk(self.d[self.root:self.root + self.ndir * 32]))


def main(argv):
    if len(argv) < 3:
        raise SystemExit(__doc__)
    fs = St(argv[2]).files()
    if argv[1] == "ls":
        for p, data in fs.items():
            print(p if data is None else "%s  %d" % (p, len(data)))
    elif argv[1] == "caves":
        pc = open(argv[3] if len(argv) > 3 else "extracted/ega1/CELLMAPS.BIN", "rb").read()
        rec = [pc[i * CAVE:(i + 1) * CAVE] for i in range(40)]
        blob = fs["DATA.D"][-20 * CAVE:]
        for k in range(20):
            c = blob[k * CAVE:(k + 1) * CAVE]
            border = len(set(c[:40])) == 1 and len(set(c[-40:])) == 1
            best = max(range(40), key=lambda i: sum(a == b for a, b in zip(c, rec[i])))
            s = sum(a == b for a, b in zip(c, rec[best])) / CAVE
            # Below about 90% the likeness is mostly shared empty cells, so the
            # cave is new or rebuilt, not the PC's cave with a few cells moved.
            kind = "exact" if s == 1 else ("altered" if s >= 0.9 else "new or rebuilt")
            print("ST cave %2d  %-12s %5.1f%%  %-10s %s" % (
                k + 1, name(best), 100 * s, kind, "" if border else "(no steel border!)"))
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv)
