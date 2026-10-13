#!/usr/bin/env python3
"""The Amiga game's own per-world, per-cave parameters, against the PC's.

    python3 tools/amigaworlds.py <disk.adf> [caves.json]
    python3 tools/amigaworlds.py --selftest <disk.adf>

`rf` keeps ten 72-byte world records from offset 0x55F8 of its code, in the PC's
raw-world order (hunter, scuba, cook, player, cowboy, music, space, luck, body,
miner). Each is the world's name in its first 24 bytes, then, read by the routine
at 0x2820:

    +0x18  four longwords, one a cave. Not identified; 45 in the last five worlds'
           caves, which looks like a default.
    +0x38  four words: the treasure quota, a cave at a time.
    +0x40  four words: the time limit in seconds.

The quota and the time are the same two tables the PC keeps at ds:0x245E and
ds:0x26A2 (docs/FORMATS.md), so the two can be set side by side.
"""
import json
import struct
import sys

sys.path.insert(0, __file__.rsplit("/", 1)[0])
import amigacols  # noqa: E402

RECORDS = 0x55F8
STRIDE = 0x48
HUNK_CODE = 0x20          # the code hunk starts here in the file, after the header
CODE_LEN = 26332


def records(rf):
    code = rf[HUNK_CODE:HUNK_CODE + CODE_LEN]
    out = []
    for w in range(10):
        r = code[RECORDS + w * STRIDE:RECORDS + (w + 1) * STRIDE]
        out.append({
            "name": r[:0x18].split(b"\0")[0].decode("latin1").strip(),
            "quota": list(struct.unpack(">4H", r[0x38:0x40])),
            "time": list(struct.unpack(">4H", r[0x40:0x48])),
            "extra": list(struct.unpack(">4L", r[0x18:0x28])),
        })
    return out


def compare(recs, pc):
    rows = []
    for w, r in enumerate(recs):
        for n in range(4):
            i = w * 4 + n
            rows.append((amigacols.WORLDS[w], n + 1, r["quota"][n], pc["required"][i],
                         r["time"][n], pc["time"][i]))
    return rows


def selftest(adf):
    recs = records(adf.file("rf"))
    assert [r["name"] for r in recs][:3] == ["The HUNTER", "The SCUBA", "The COOK"], recs[0]
    # Read by hand on 12 October 2026 and set against caves.json.
    assert recs[0]["quota"] == [40, 40, 80, 100], recs[0]      # PC: 150 for the fourth
    assert recs[7]["quota"] == [20, 8, 40, 20], recs[7]        # luck; PC: 15, 11, 30, 65
    assert recs[9]["time"] == [140, 140, 140, 140], recs[9]    # miner; PC: 140 140 110 180
    assert recs[1]["quota"] == [20, 65, 130, 55]               # scuba: the PC's, exactly
    print("amigaworlds: selftest ok")


def main(argv):
    if len(argv) >= 3 and argv[1] == "--selftest":
        return selftest(amigacols.Adf(argv[2]))
    if len(argv) < 2 or argv[1] in ("-h", "--help"):
        print(__doc__)
        return
    adf = amigacols.Adf(argv[1])
    pc = json.load(open(argv[2] if len(argv) > 2 else "web/Rockford.Web/wwwroot/game/caves.json"))
    recs = records(adf.file("rf"))
    print("%-7s %s  %-13s %-13s  %-15s %-15s" % ("world", "n", "quota A/PC", "", "time A/PC", ""))
    for world, n, qa, qp, ta, tp in compare(recs, pc):
        flag = "" if qa == qp and ta == tp else "  <- differs"
        print("%-7s %d  %4d / %-4d     %4d / %-4d%s" % (world, n, qa, qp, ta, tp, flag))


if __name__ == "__main__":
    main(sys.argv)
