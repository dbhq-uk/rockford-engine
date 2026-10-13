#!/usr/bin/env python3
"""The .SCM and .CCM run-length format, in one place.

Both are byte-oriented RLE over 8-bit palette indices. The game's own decoder is
at raw offset 0x1275B and it is nine instructions:

    0x80      ->  END. Exactly this byte, and no value byte follows it.
    0x00      ->  nothing. A no-op the format allows and no shipped file uses.
    n & 0x80  ->  a run of (n & 0x7F) copies of the next byte
    otherwise ->  n literal bytes follow

0x80 being the terminator is why a zero-length run cannot be encoded: it would be
that same byte. Reading it as a run instead swallows the byte after it, which on
a real file is the first byte of the palette.

THE STREAM IS BOUNDED BY THE PIXEL COUNT, NOT BY THE END OF THE FILE. A screen
is exactly 64,000 pixels, and what follows is the terminator and the palette - so
a decoder that runs to EOF turns the palette into image data. It does not look
like a failure either: it returns a slightly-too-long buffer that a caller
happily treats as pixels. Every shipped .SCM overshot by 147 to 156 bytes.

The game does not count pixels at all; it decodes until the terminator and lets
the last write run past the screen. Most of these files do exactly that - they
carry one more literal pixel after the 64,000th, which lands past the visible
screen and inside the same 64 KiB segment, so it is harmless there. It is why a
.SCM tail is 195 bytes rather than 193: one literal count, one pixel, the
terminator, and 192 bytes of palette. The two that end on the count exactly have
193. A .CCM has 3 and no palette - it borrows one.

This module exists because that decoder was written twice - once correctly in
webassets.py and once not in scm.py - and the wrong copy was the one anybody
would read first.

    python3 tools/scmformat.py --selftest

runs the checks below against synthetic streams, so it needs no game data.
"""
import sys

#: A screen: 320 by 200 at one byte a pixel.
PIXELS = 64000

#: The end of the stream. 0x1275C tests for exactly this, before anything else.
END = 0x80


class ScmError(ValueError):
    """The stream does not decode to a whole screen."""


def decode(data, limit=PIXELS):
    """Decode until `limit` pixels have been produced.

    Returns `(pixels, consumed)` - the pixel bytes, and how far into `data` the
    stream reached. The caller needs the second one: the palette starts there,
    and there is no other way to find it.

    Raises `ScmError` rather than returning something short or long. A truncated
    file and a file whose runs do not land on the pixel count are both real
    faults, and silently handing back a buffer of the wrong length is how this
    went unnoticed in the first place.
    """
    out = bytearray()
    i = 0
    n = len(data)

    while len(out) < limit:
        if i >= n:
            raise ScmError(
                f'stream ended after {len(out)} pixels, expected {limit}')

        c = data[i]
        i += 1

        if c == END:
            # 0x1275C. A screen that ends early is not a screen, and reading
            # this as a zero-length run - which is what `c & 0x80` does - eats
            # the byte after it and carries on into the palette.
            raise ScmError(
                f'terminator at {i - 1} after {len(out)} pixels, expected {limit}')

        if c == 0:
            # 0x12760 jumps straight back to the next command. Allowed by the
            # format and used by none of the shipped files.
            continue

        if c & 0x80:
            run = c & 0x7F
            if i >= n:
                raise ScmError(f'run of {run} at {i - 1} has no value byte')
            if len(out) + run > limit:
                raise ScmError(
                    f'run of {run} at {i - 1} overshoots {limit} '
                    f'by {len(out) + run - limit}')
            out += bytes([data[i]]) * run
            i += 1
        else:
            if i + c > n:
                raise ScmError(
                    f'literal of {c} at {i - 1} runs {i + c - n} past the end')
            if len(out) + c > limit:
                raise ScmError(
                    f'literal of {c} at {i - 1} overshoots {limit} '
                    f'by {len(out) + c - limit}')
            out += data[i:i + c]
            i += c

    return bytes(out), i


# ---- the checks ---------------------------------------------------------
#
# Synthetic, so they run on a clean checkout. The game files are private and CI
# does not have them, which is exactly why the real decoder went unchecked.

def _selftest():
    failures = []

    def check(name, fn):
        # Every exception, not only AssertionError. A decoder that raises where
        # it should not is a failure of this check rather than a crash of the
        # harness, and the harness crashing hides every check after it.
        try:
            fn()
            print(f'  ok    {name}')
        except Exception as e:
            failures.append(name)
            print(f'  FAIL  {name}\n        {type(e).__name__}: {e}')

    def literal(payload):
        """A stream of literal runs carrying exactly `payload`."""
        out = bytearray()
        for i in range(0, len(payload), 0x7F):
            chunk = payload[i:i + 0x7F]
            out.append(len(chunk))
            out += chunk
        return bytes(out)

    def run(count, value):
        return bytes([0x80 | count, value])

    print('scmformat')

    def stops_at_the_pixel_count():
        # The bug, in one case. A run of pixels followed by a palette that
        # happens to decode: the decoder must not reach it.
        stream = run(0x7F, 0xAA) * (PIXELS // 0x7F) \
            + run(PIXELS % 0x7F, 0xAA) \
            + b'\x7f' + b'\xDE' * 0x7F          # the "palette"
        px, consumed = decode(stream)
        assert len(px) == PIXELS, f'{len(px)} pixels'
        assert set(px) == {0xAA}, 'palette bytes reached the image'
        assert consumed == len(stream) - 0x80, f'consumed {consumed}'

    def the_offset_finds_the_tail():
        stream = run(0x7F, 1) * (PIXELS // 0x7F) + run(PIXELS % 0x7F, 1)
        tail = b'the palette goes here'
        px, consumed = decode(stream + tail)
        assert (stream + tail)[consumed:] == tail, 'the tail is in the wrong place'

    def literals_and_runs_mix():
        body = bytes(range(256)) * 4
        stream = literal(body) + run(0x7F, 9) * ((PIXELS - len(body)) // 0x7F) \
            + run((PIXELS - len(body)) % 0x7F, 9)
        px, _ = decode(stream)
        assert len(px) == PIXELS, f'{len(px)} pixels'
        assert px[:len(body)] == body, 'the literals came out wrong'

    def a_short_stream_is_an_error():
        try:
            decode(run(0x7F, 0) * 8)
        except ScmError as e:
            assert 'expected' in str(e), str(e)
        else:
            raise AssertionError('a stream 63,000 pixels short decoded fine')

    def a_run_past_the_count_is_an_error():
        # One pixel short, then a run of two.
        stream = run(0x7F, 3) * (PIXELS // 0x7F) + run(PIXELS % 0x7F - 1, 3) + run(2, 3)
        try:
            decode(stream)
        except ScmError as e:
            assert 'overshoots' in str(e), str(e)
        else:
            raise AssertionError('a run past 64,000 decoded fine')

    def a_truncated_literal_is_an_error():
        try:
            decode(b'\x10abc')
        except ScmError as e:
            assert 'past the end' in str(e), str(e)
        else:
            raise AssertionError('a literal running off the end decoded fine')

    def a_zero_count_is_a_no_op():
        # 0x12760 jumps back to the next command. It is allowed, and rejecting
        # it was stricter than the game - this check used to assert the
        # opposite, which is a test pinning a restriction the format does not
        # have.
        screen = run(0x7F, 5) * (PIXELS // 0x7F) + run(PIXELS % 0x7F, 5)
        px, _ = decode(b'\x00\x00' + screen)
        assert len(px) == PIXELS, f'{len(px)} pixels'
        assert set(px) == {5}, 'the no-op left something behind'

    def an_early_terminator_is_an_error():
        # 0x80 is the END marker and consumes no value byte. Reading it as a
        # zero-length run swallows the byte after it, which on a real file is
        # the first byte of the palette - so the stream carries on decoding
        # the palette as pixels and lands on a plausible-looking screen.
        screen = run(0x7F, 7) * (PIXELS // 0x7F) + run(PIXELS % 0x7F, 7)
        try:
            decode(b'\x80\x99' + screen)
        except ScmError as e:
            assert 'terminator' in str(e), str(e)
        else:
            raise AssertionError('a stream that ends before the screen decoded fine')

    def a_literal_past_the_count_is_an_error():
        # Separate from the run case. Disabling only the literal bound left
        # every other check green.
        stream = run(0x7F, 3) * (PIXELS // 0x7F) + run(PIXELS % 0x7F - 1, 3) \
            + b'\x02ab'
        try:
            decode(stream)
        except ScmError as e:
            assert 'literal' in str(e) and 'overshoots' in str(e), str(e)
        else:
            raise AssertionError('a literal past 64,000 decoded fine')

    check('the stream stops at the pixel count', stops_at_the_pixel_count)
    check('the consumed offset points at the tail', the_offset_finds_the_tail)
    check('literals and runs mix', literals_and_runs_mix)
    check('a short stream is an error', a_short_stream_is_an_error)
    check('a run past the count is an error', a_run_past_the_count_is_an_error)
    check('a truncated literal is an error', a_truncated_literal_is_an_error)
    check('a zero count is a no-op', a_zero_count_is_a_no_op)
    check('an early terminator is an error', an_early_terminator_is_an_error)
    check('a literal past the count is an error', a_literal_past_the_count_is_an_error)

    print('\nall good' if not failures else f'\n{len(failures)} failed')
    return 0 if not failures else 1


if __name__ == '__main__':
    if '--selftest' in sys.argv:
        sys.exit(_selftest())
    print(__doc__.strip())
