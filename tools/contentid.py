#!/usr/bin/env python3
"""One identity for everything a release serves that Blazor does not fingerprint.

    python3 tools/contentid.py [--version | --hash]

WHY A RELEASE NEEDS ONE.

Blazor fingerprints `_framework`, so the code invalidates itself correctly. The
rest of `wwwroot` - the atlases, the game data, the audio, the stylesheet - had
stable URLs and a four-hour Cloudflare Pages cache, so a new application could
run against the previous release's presentation assets. Nothing observed that
happening; the repository simply permitted it.

And the one cache key that did exist was a CLOCK. `Version` in the project file
was `1.0.<day-of-year>.<HHmm>`, which means two builds made inside the same
minute are indistinguishable to a browser, and two builds of identical content
made an hour apart needlessly are not. Both directions are wrong: a cache key
should follow the CONTENT.

WHAT IS HASHED.

Every file under `web/Rockford.Web/wwwroot`, by relative path and by bytes, in
sorted order. The path is hashed as well as the content so that renaming a file
changes the identity - two files swapping names is otherwise invisible.

Deliberately the source tree rather than either published output. The identity
has to be known BEFORE `dotnet publish`, because it is compiled in as the
assembly version and the application uses it to build its own asset URLs; a hash
of the published output could only be applied afterwards, and by then the
application that has to quote it is already built.

The consequence is that the faithful and VIP builds share an identity even
though they ship different subsets. That is harmless - each build only ever asks
for its own files - and it is the property that matters: any change to any asset
changes the identity, and therefore changes every asset URL.
"""
import hashlib
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WWWROOT = ROOT / 'web/Rockford.Web/wwwroot'


def content_hash():
    """A sha256 over every asset, by path and by bytes."""
    h = hashlib.sha256()
    for path in sorted(WWWROOT.rglob('*')):
        if not path.is_file():
            continue
        h.update(path.relative_to(WWWROOT).as_posix().encode())
        h.update(b'\0')
        h.update(path.read_bytes())
        h.update(b'\0')
    return h.hexdigest()


def version(digest):
    """The hash as a .NET version quad.

    `<Version>` has to be numeric, so the first 32 bits become the last two
    fields. 1.0 stays the product version and carries no information.

    **65534, not 65535.** A C# assembly version component may not be 65535 -
    that value is reserved, and the compiler rejects it with CS7034 - so the
    naive 16-bit truncation had a roughly one-in-32,000 chance per build of
    emitting a version that will not compile. It would have been a release that
    failed to build for no reason anybody could reproduce. Astra, 19 September
    2026.

    16 bits a field is not a lot, but this is a cache key rather than a
    signature: the cost of a collision is a browser keeping an asset it should
    have refetched, and the alternative was a clock with a one-minute
    resolution.
    """
    return f'1.0.{int(digest[0:4], 16) % 65535}.{int(digest[4:8], 16) % 65535}'


def selftest():
    """The two properties a cache key has to have, on a synthetic tree.

    Not on wwwroot, because a test that writes into the real asset tree to prove
    a point is a test that can leave it modified.
    """
    import tempfile

    fails = []

    def ok(what, got, want):
        if got != want:
            fails.append(f'{what}: got {got}, wanted {want}')

    global WWWROOT
    real = WWWROOT
    try:
        with tempfile.TemporaryDirectory() as d:
            WWWROOT = Path(d)
            (WWWROOT / 'sub').mkdir()
            (WWWROOT / 'a.png').write_bytes(b'one')
            (WWWROOT / 'sub' / 'b.json').write_bytes(b'two')

            first = content_hash()
            ok('the same content gives the same identity', content_hash(), first)

            # A changed asset must change it, or a release can serve stale art.
            (WWWROOT / 'a.png').write_bytes(b'ONE')
            changed = content_hash()
            ok('a changed byte changes the identity', changed != first, True)

            # And back again, so it follows the content rather than the clock -
            # which is the half the old build-time stamp got wrong in the other
            # direction, discarding a warm cache for an identical build.
            (WWWROOT / 'a.png').write_bytes(b'one')
            ok('reverting the change restores the identity', content_hash(), first)

            # A rename with no content change must also move it. Hashing bytes
            # alone cannot see two files swapping names.
            (WWWROOT / 'a.png').rename(WWWROOT / 'c.png')
            ok('a rename changes the identity', content_hash() != first, True)

            # The version quad has to be legal for MSBuild: four numeric fields,
            # and the last two under 65536.
            quad = version(first).split('.')
            ok('the version has four fields', len(quad), 4)
            ok('every field is numeric', all(f.isdigit() for f in quad), True)
            ok('the fields are legal assembly version components',
               max(int(f) for f in quad) <= 65534, True)

            # The boundary itself, not just a sample. 65535 is the value the
            # compiler rejects, and the first version of this test asserted
            # "< 65536", which passes on exactly the digest that breaks.
            ok('a digest that would give 65535 is brought inside the range',
               version('ffffffff' + '0' * 56), '1.0.0.0')
    finally:
        WWWROOT = real

    for line in fails:
        print(f'  FAIL  {line}')
    print(f'contentid: {"all good" if not fails else f"{len(fails)} failed"}')
    return 1 if fails else 0


def main():
    if '--selftest' in sys.argv:
        return selftest()

    if not WWWROOT.is_dir():
        print(f'no wwwroot at {WWWROOT}', file=sys.stderr)
        return 1

    digest = content_hash()
    if '--hash' in sys.argv:
        print(digest)
    else:
        print(version(digest))
    return 0


if __name__ == '__main__':
    sys.exit(main())
