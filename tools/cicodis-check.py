#!/usr/bin/env python3
"""Re-run the cicodis feasibility findings, and fail if any has stopped holding.

    python3 tools/cicodis-check.py [path-to-cicodis-ROCKFORD.EXE]
    python3 tools/cicodis-check.py --selftest

The findings in docs/CICODIS-FEASIBILITY.md are the kind that go stale without
anyone noticing: they are about two binaries, and either could be replaced. This
re-derives them rather than leaving the document as the only record.

EVERY FINDING IS ASSERTED, not printed. The first version of this file reported
the numbers and returned 0 whatever they were, so all three of its findings could
be falsified and it still passed - Astra demonstrated exactly that by mutating
the binaries in memory and watching it exit clean. A check that cannot fail is
worse than no check, because it reports green.

The cicodis executable is not committed here - it belongs to that project, and
this repository already holds more of somebody else's data than it would like.
Its `unpacked.zip` is at:

    gabonator/Projects @ a4809cf62 : CicoJit/gamelib/rockford/unpacked.zip

Without it, the checks that need only our own files still run and still fail.
"""
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
VGA = ROOT / 'unpacked/vga.bin'
EGA = ROOT / 'unpacked/ega.bin'

# The engine addresses PORT-PLAN.md and FORMATS.md cite. The finding is that
# every one of them is a VGA offset and the EGA build has nothing there.
CITED = {
    'random generator': 0x124B1,
    'scan loop': 0x140DE,
    'dispatch table': 0x13C50,
    'player handler': 0x1356C,
    'viewport scroll': 0x0FF87,
    'timed wall': 0x1467F,
}

# One routine's prologue is shared, and only its prologue. Seventeen bytes of
# agreement is a compiler emitting the same entry sequence, not a shared body.
SHARED_PREFIX = ('player handler', 0x1356C, 0x21A91, 17)

# Past that length nothing matches at all, which is what "separately compiled"
# means in evidence rather than in assertion.
NO_MATCH_ABOVE = 18

MZ_HEADER = 160
FILLER_AT = (0x1CFA4, 0x1CFB0)          # where the two executables differ
FILLER = b'K   STACK   '                 # what ours has there


def findings(vga, ega, other):
    """Every finding, as (held, description). Nothing is printed here."""
    out = []

    # 1. The cited offsets are VGA, and the EGA build is empty at all of them.
    blank = [n for n, off in CITED.items() if set(ega[off:off + 12]) == {0}]
    out.append((len(blank) == len(CITED),
                f'all {len(CITED)} cited offsets are zeros in ega.bin '
                f'(found {len(blank)})'))

    # 2. Exactly one routine's bytes appear anywhere, and it is the known one,
    #    at the known place. "How many" is not enough - a different single match
    #    would be a different finding wearing this one's number.
    name, vga_at, ega_at, length = SHARED_PREFIX
    matches = {n: ega.find(vga[off:off + 16]) for n, off in CITED.items()}
    hit = {n: at for n, at in matches.items() if at >= 0}
    out.append((list(hit) == [name] and hit[name] == ega_at,
                f'the only 16-byte match is {name} at 0x{ega_at:X} '
                f'(found {hit})'))

    # 3. And it is a prologue: it matches at its length and not beyond.
    out.append((ega.find(vga[vga_at:vga_at + length]) == ega_at,
                f'{name} matches at {length} bytes'))
    out.append((all(ega.find(vga[off:off + NO_MATCH_ABOVE]) < 0
                    for off in CITED.values()),
                f'nothing matches at {NO_MATCH_ABOVE} bytes or more'))

    if other is None:
        return out

    # 4. The executable is ours, and differs ONLY in the stack filler - at that
    #    exact place, with ours reading the filler and theirs reading zeros.
    #    The first version checked only that our side's bytes appeared somewhere
    #    in the filler string, which passes for a one-byte change to a space
    #    anywhere in the file.
    body = other[MZ_HEADER:]
    lo, hi = FILLER_AT
    out.append((len(body) == len(ega),
                f'the bodies are the same length ({len(body)} against {len(ega)})'))

    if len(body) != len(ega):
        return out

    diff = [i for i, (a, b) in enumerate(zip(body, ega)) if a != b]
    out.append((diff == list(range(lo, hi)),
                f'they differ at exactly 0x{lo:X}..0x{hi - 1:X} '
                f'({len(diff)} bytes differing)'))
    out.append((ega[lo:hi] == FILLER, 'ours reads the stack filler there'))
    out.append((set(body[lo:hi]) == {0}, 'theirs reads zeros there'))
    return out


def selftest():
    """The check has to FAIL when a finding stops holding."""
    if not VGA.exists() or not EGA.exists():
        print('contentid: no binaries, selftest skipped')
        return 0

    vga = bytearray(VGA.read_bytes())
    ega = bytearray(EGA.read_bytes())
    other = bytearray(b'\0' * MZ_HEADER) + bytearray(ega)
    lo, hi = FILLER_AT
    other[MZ_HEADER + lo:MZ_HEADER + hi] = b'\0' * (hi - lo)

    fails = []

    def expect(what, mutate, holds):
        v, e, o = bytearray(vga), bytearray(ega), bytearray(other)
        mutate(v, e, o)
        got = all(ok for ok, _ in findings(bytes(v), bytes(e), bytes(o)))
        if got != holds:
            fails.append(f'{what}: the check said {got}, wanted {holds}')

    expect('the findings as they stand', lambda v, e, o: None, True)

    # The three false passes Astra demonstrated.
    def unzero(v, e, o):
        e[0x124B1] = 0x90
    expect('a cited offset that is no longer zeros', unzero, False)

    def unmatch(v, e, o):
        e[0x21A91:0x21A91 + 17] = b'\x90' * 17
    expect('the shared prologue destroyed', unmatch, False)

    def elsewhere(v, e, o):
        o[MZ_HEADER + 0x3575] = 0xCC
    expect('an executable differing away from the filler', elsewhere, False)

    # And one more: a difference of the right size in the wrong place.
    def moved(v, e, o):
        o[MZ_HEADER + lo:MZ_HEADER + hi] = e[lo:hi]
        o[MZ_HEADER + 0x4000:MZ_HEADER + 0x400C] = b'\0' * 12
    expect('the difference moved somewhere else', moved, False)

    # AND THAT THE TOOL ITSELF FAILS, not only that findings() discriminates.
    # Gutting main's failure count leaves every check above passing, which is
    # the original defect one level up.
    global findings
    real = findings
    try:
        findings = lambda *_: [(False, 'a finding that does not hold')]
        if main_status() == 0:
            fails.append('main reported success on a failing finding')
        findings = lambda *_: [(True, 'a finding that holds')]
        if main_status() != 0:
            fails.append('main reported failure on a passing finding')
    finally:
        findings = real

    for line in fails:
        print(f'  FAIL  {line}')
    print(f'cicodis selftest: {"all good" if not fails else f"{len(fails)} failed"}')
    return 1 if fails else 0


def main_status():
    """main's exit code, with its output swallowed."""
    import contextlib
    import io

    with contextlib.redirect_stdout(io.StringIO()):
        return main(['cicodis-check'])


def main(argv=None):
    argv = sys.argv if argv is None else argv

    # CI asserts that every tool here answers --help without a traceback, and
    # this one took it for a path.
    if '--help' in argv or '-h' in argv:
        print(__doc__.strip())
        return 0

    if '--selftest' in argv:
        return selftest()

    if not VGA.exists() or not EGA.exists():
        print('no unpacked/vga.bin and unpacked/ega.bin - nothing to check')
        return 0

    given = [a for a in argv[1:] if not a.startswith('-')]
    other = Path(given[0]).read_bytes() if given else None

    results = findings(VGA.read_bytes(), EGA.read_bytes(), other)
    for ok, description in results:
        print(f'  {"ok  " if ok else "FAIL"}  {description}')

    if other is None:
        print('\nno cicodis executable given, so the binary comparison was skipped')

    failed = sum(1 for ok, _ in results if not ok)
    print(f'\ncicodis: {"as documented" if not failed else f"{failed} findings no longer hold"}')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
