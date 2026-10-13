#!/usr/bin/env python3
"""Read an Amiga hunk executable and disassemble its 68000 code.

    python3 tools/amigahunk.py info    <exe>
    python3 tools/amigahunk.py strings <exe> [min-length]
    python3 tools/amigahunk.py refs    <exe> <lo> <hi>        # pointers into a range
    python3 tools/amigahunk.py disasm  <exe> <start> <end>    # needs: pip install capstone

Only the single-code-hunk case is handled, which is all `rf`, the Amiga game, is.
Addresses are offsets into that hunk, the way the program sees them with the load
address taken away. Code and data are mixed in the hunk, so a disassembly of a data
region is meaningless; Capstone is resynchronised on failure, nothing more.
`refs` lists the longwords the file's own relocation table marks as pointers. A
pc-relative reference has no relocation, so for those read the disassembly:
Capstone prints the resolved target, as in `lea.l $5116(pc), a0`.
"""
import re
import struct
import sys


def be(b, o):
    return struct.unpack(">I", b[o:o + 4])[0]


class Hunk:
    def __init__(self, path):
        d = self.d = open(path, "rb").read()
        if be(d, 0) != 0x3F3:
            raise SystemExit("%s: not an Amiga hunk executable" % path)
        o = 4
        while be(d, o):
            o += 4 + be(d, o) * 4
        o += 4
        _, first, last = be(d, o), be(d, o + 4), be(d, o + 8)
        o += 12 + 4 * (last - first + 1)
        if first != last or be(d, o) != 0x3E9:
            raise SystemExit("%s: more than one hunk, not handled" % path)
        n = be(d, o + 4) * 4
        self.code = d[o + 8:o + 8 + n]
        o += 8 + n
        self.relocs = []
        if be(d, o) == 0x3EC:
            o += 4
            while be(d, o):
                n, o = be(d, o), o + 8
                self.relocs += [be(d, o + 4 * i) for i in range(n)]
                o += 4 * n
        self.relocs.sort()

    def long(self, off):
        return be(self.code, off)


def disasm(code, start, end):
    from capstone import Cs, CS_ARCH_M68K, CS_MODE_M68K_000
    md = Cs(CS_ARCH_M68K, CS_MODE_M68K_000)
    off = start
    while off < end:
        for ins in md.disasm(code[off:end], off):
            print("%04x  %-8s %s" % (ins.address, ins.mnemonic, ins.op_str))
            off = ins.address + ins.size
            break
        else:
            off += 2


def main(argv):
    if len(argv) < 3:
        raise SystemExit(__doc__)
    h = Hunk(argv[2])
    if argv[1] == "info":
        print("code %d bytes, %d relocations" % (len(h.code), len(h.relocs)))
    elif argv[1] == "strings":
        n = int(argv[3]) if len(argv) > 3 else 4
        for m in re.finditer(rb"[ -~]{%d,}" % n, h.code):
            print("%04x  %s" % (m.start(), m.group().decode("latin1")))
    elif argv[1] == "refs":
        lo, hi = int(argv[3], 0), int(argv[4], 0)
        for r in h.relocs:
            if lo <= h.long(r) < hi:
                print("%04x -> %04x" % (r, h.long(r)))
    elif argv[1] == "disasm":
        disasm(h.code, int(argv[3], 0), int(argv[4], 0))
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    main(sys.argv)
