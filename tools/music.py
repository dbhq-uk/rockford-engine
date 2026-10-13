#!/usr/bin/env python3
"""Extract the PC-speaker music from an unpacked Rockford executable.

The tune is a TRACKER, which an earlier reading of this file got wrong. Structure,
from the player at 0x1496C-0x14B13 in the VGA image:

    tempo        one word just before the note table. A sub-step lasts
                 tempo + 1 ticks of the 60 Hz timer, so 166.66 ms. It is a
                 constant 9 and nothing ever writes it.

    note table   36 little-endian words of 8253 PIT divisors. The player
                 MULTIPLIES EVERY ENTRY BY FOUR at 0x14932 before a note is
                 played. Miss that and the tune comes out between 1695 Hz and
                 12693 Hz, which is a whistle rather than music.

    patterns     22 patterns of 8 events, two bytes each: a note index and a
                 gate length in sub-steps. Note 0xFF does not mean rest - it
                 means "nothing new here", and the gate counter from the last
                 real note keeps running. The speaker is gated off when that
                 counter hits zero, and a new note pre-empts it. That
                 pre-emption is the whole articulation model.

    order lists  three lists of 48 pattern numbers ending in 0xFFFF. They are
                 NOT three simultaneous voices. They are arrangements for two
                 different sound chips, chosen at 0x14973 by a byte the startup
                 probe sets after scanning the BIOS for the string "Tandy". On
                 an ordinary PC the game plays the THIRD list on the speaker,
                 monophonically. The first two drive a Tandy's two channels, and
                 on that path the third list is loaded and discarded.

The decisive instruction is 0x149B5. The value fetched from the order list is
shifted left three times and used to index the pattern bank, so it is a pattern
number and not a note. Read the bank straight through, as this tool used to, and
you get 176 events of gibberish. Read it through the order list and you get a
64-second theme in G sharp minor, structured A B C, A B C, coda.

The player runs from the timer interrupt, once per tick, gated by a flag that is
set in the attract loop at 0xED62 and cleared when a game starts. So this is the
title theme. There is no in-game music at all. The cave-complete jingle is the
same tune started at order position 44, which is the last four bars of its own
coda.
"""
import math
import os
import struct
import sys

PIT = 1193180
TICK_HZ = PIT / 0x4DAE          # 60.0, from the timer setup at 0x12DD3
NOTE_COUNT = 36
SPEAKER_SCALE = 4               # 0x14932
TEMPO_FROM_TABLE = -8
PATTERNS_FROM_TABLE = 0x90
PATTERN_STEPS = 8
PATTERN_COUNT = 22
ORDER_LENGTH = 48

# Three order lists, 49 words apart. The speaker plays the third.
ORDERS_FROM_TABLE = (0x1F0, 0x252, 0x2B4)
SPEAKER_ORDER = 2

# The cave-complete jingle is this tune from here on: the last four bars of its
# own coda. Set at 0x14B29.
JINGLE_FROM = 0x2C

NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B']


def find_note_table(d):
    """36 divisors falling a semitone at a time, found by shape not by offset,
    so the EGA image works as well as the VGA one."""
    for off in range(0, len(d) - 2 * NOTE_COUNT, 2):
        w = struct.unpack_from(f'<{NOTE_COUNT}H', d, off)
        if any(x < 64 for x in w):
            continue
        if not all(w[i] > w[i + 1] for i in range(NOTE_COUNT - 1)):
            continue
        if not all(abs(w[i] / w[i + 1] - 2 ** (1 / 12)) < 0.02 for i in range(NOTE_COUNT - 1)):
            continue
        if abs((w[0] / w[-1]) / 2 ** ((NOTE_COUNT - 1) / 12) - 1) < 0.02:
            return off, [v * SPEAKER_SCALE for v in w]
    raise LookupError("no 36-word chromatic divisor table in this image")


def read_song(d):
    off, table = find_note_table(d)
    tempo = struct.unpack_from('<H', d, off + TEMPO_FROM_TABLE)[0]

    bank = off + PATTERNS_FROM_TABLE
    patterns = [[(d[bank + p * PATTERN_STEPS * 2 + 2 * i],
                  d[bank + p * PATTERN_STEPS * 2 + 2 * i + 1])
                 for i in range(PATTERN_STEPS)]
                for p in range(PATTERN_COUNT)]

    orders = []
    for base in ORDERS_FROM_TABLE:
        o = off + base
        orders.append([struct.unpack_from('<H', d, o + 2 * i)[0] for i in range(ORDER_LENGTH)])

    return {
        'note_table_at': off,
        'table': table,
        'tempo': tempo,
        'substep': (tempo + 1) / TICK_HZ,
        'patterns': patterns,
        'orders': orders,
    }


def events(song, order, start=0):
    """Flatten the song into (divisor or None, seconds) in playing order.

    A note with no following 0xFF runs for one sub-step, because the next event
    pre-empts its gate. That pre-emption is why the tune has a rhythm at all.
    """
    out = []
    held = 0
    current = None

    for pos in order[start:]:
        if pos == 0xFFFF:
            break
        for note, gate in song['patterns'][pos]:
            if note != 0xFF:
                current = song['table'][note]
                held = gate
            elif held > 0:
                held -= 1
                if held == 0:
                    current = None
                    held = 1
            out.append((current, song['substep']))
    return out


def midi_number(freq):
    return int(round(12 * math.log2(freq / 440.0) + 69))


def write_midi(path, song, order, start=0):
    """One monophonic track, at the real tempo. A quarter note is four sub-steps."""
    ppq = 4
    trk = bytearray()

    def vlq(n):
        out = [n & 0x7F]
        n >>= 7
        while n:
            out.append((n & 0x7F) | 0x80)
            n >>= 7
        return bytes(reversed(out))

    tempo_us = int(round(song['substep'] * 4 * 1_000_000))
    trk += b'\x00\xff\x51\x03' + tempo_us.to_bytes(3, 'big')

    flat = events(song, order, start)
    pending = 0
    i = 0
    while i < len(flat):
        div, _ = flat[i]
        run = 1
        while i + run < len(flat) and flat[i + run][0] == div:
            run += 1

        if div is None:
            pending += run
        else:
            m = max(0, min(127, midi_number(PIT / div)))
            trk += vlq(pending) + bytes([0x90, m, 100])
            trk += vlq(run) + bytes([0x80, m, 0])
            pending = 0
        i += run

    trk += b'\x00\xff\x2f\x00'
    hdr = (b'MThd' + (6).to_bytes(4, 'big') + (0).to_bytes(2, 'big')
           + (1).to_bytes(2, 'big') + ppq.to_bytes(2, 'big'))
    with open(path, 'wb') as f:
        f.write(hdr + b'MTrk' + len(trk).to_bytes(4, 'big') + bytes(trk))


def main():
    from usage import wants_help, help_and_exit
    if wants_help():
        help_and_exit()

    src = sys.argv[1] if len(sys.argv) > 1 else 'unpacked/vga.bin'
    out = sys.argv[2] if len(sys.argv) > 2 else 'music/original'
    d = open(src, 'rb').read()
    os.makedirs(out, exist_ok=True)

    song = read_song(d)
    table = song['table']
    print(f"{src}: note table @0x{song['note_table_at']:05X}, scaled x{SPEAKER_SCALE}, "
          f"{PIT / table[0]:.1f} Hz down to {PIT / table[-1]:.1f} Hz")
    print(f"tempo {song['tempo']} -> a sub-step is {song['substep'] * 1000:.2f} ms "
          f"at {TICK_HZ:.1f} Hz")

    order = song['orders'][SPEAKER_ORDER]
    used = sorted(set(order))
    print(f"\n{PATTERN_COUNT} patterns, {len(used)} of them used: {used}")

    flat = events(song, order)
    notes = sum(1 for div, _ in flat if div is not None)
    print(f"order list of {len(order)} -> {len(flat)} sub-steps, "
          f"{sum(s for _, s in flat):.1f} seconds")

    opens = [NAMES[midi_number(PIT / div) % 12] for div, _ in flat[:16] if div]
    print(f"opens: {' '.join(opens)}")

    write_midi(os.path.join(out, 'theme.mid'), song, order)
    print(f"  -> {os.path.join(out, 'theme.mid')}")

    write_midi(os.path.join(out, 'jingle.mid'), song, order, start=JINGLE_FROM)
    print(f"  -> {os.path.join(out, 'jingle.mid')}  (cave complete, from order {JINGLE_FROM})")


if __name__ == '__main__':
    main()
