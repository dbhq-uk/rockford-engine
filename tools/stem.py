"""Render the extracted tune to a plain stem WAV.

    python3 tools/stem.py [unpacked/vga.bin] [out-dir]

Writes ``theme-stem.wav`` and ``jingle-stem.wav`` into the output directory,
default ``music/original``.

A stem is the tune with no character: no effects, no arrangement, one note at a
time. It exists to be conditioning input for a music model, which needs to hear
the melody rather than a performance of it. That is also why it is an octave
below the game - the original is PC speaker square wave up around 3 kHz, and a
model handed that hears a whistle rather than a tune.

The recipe is not invented. ``music/original/theme-stem.wav`` was made by hand
before this tool existed, and these numbers are measured back off it so a stem
rendered here sits beside that one rather than sounding like a second opinion:

  * an octave down - its first note reads 419.9 Hz against the game's 839.8
  * a struck envelope: peak inside 5 ms, then an exponential decay that falls to
    roughly 0.81 over 120 ms, which is a time constant near 0.56 s
  * four harmonics at 1.0, 0.305, 0.086 and 0.019, and nothing above the fourth.
    That is not a square, a saw or a triangle - a triangle has no even harmonics
    at all, and this has a strong second - so it is built additively rather than
    by picking a waveform and filtering it.

Note lengths come from the tune's own articulation, which ``music.events``
already resolves: a note runs until the next one pre-empts it, and a 0xFF byte
means "nothing new here" rather than a rest.
"""

import math
import os
import struct
import sys
import wave

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from usage import help_and_exit, wants_help  # noqa: E402

# Before anything is read: without the game data (the public repository has none)
# this crashed on `--help` instead of describing itself.
if wants_help():
    help_and_exit(sys.modules[__name__])

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import music as M  # noqa: E402

RATE = 44100
OCTAVE_DOWN = 2          # divide the frequency by this
PEAK = 0.90              # measured off the hand-made stem: 29490 of 32767
TAU = 0.56               # seconds, the decay time constant
ATTACK = 0.002           # seconds
TAIL = 2.0               # seconds a struck note is allowed to ring on for
HARMONICS = (1.0, 0.305, 0.086, 0.019)


def notes(song, order, start=0):
    """Collapse the sub-step stream into (freq or None, seconds) per struck note.

    ``music.events`` gives one entry per sub-step with the note that is sounding,
    which is the right shape for a player and the wrong one for a synthesiser: it
    would restrike the same note eight times a bar. Runs of the same frequency
    are one strike held for their combined length.
    """
    out = []
    for div, secs in M.events(song, order, start):
        hz = None if div is None else M.PIT / div / OCTAVE_DOWN
        if out and out[-1][0] == hz:
            out[-1][1] += secs
        else:
            out.append([hz, secs])
    return out


def render(struck):
    """Struck notes, overlap-added so a note rings on past the next one.

    Not one note at a time. Cutting each note dead where the next begins was the
    first attempt and it is measurably wrong: the hand-made stem peaks at 29490
    in the dense bars and only 17288 in the sparse ones, because its notes ring
    into each other and sum. A strictly monophonic render is flat at the top of
    its range everywhere, which reads as a different instrument played by a
    machine rather than the same one struck faster.

    Ringing on is also what a struck tone does, and this decay is long - a time
    constant of 0.56 s against bars of 1.33 s.
    """
    tail = int(RATE * TAIL)
    total = int(RATE * sum(s for _, s in struck)) + tail
    samples = [0.0] * total

    at = 0
    for hz, seconds in struck:
        n = int(RATE * seconds)
        if hz is None:
            at += n
            continue

        # The note is written for its own length plus the whole decay tail, and
        # whatever is already in the buffer is added to rather than replaced.
        for i in range(min(n + tail, total - at)):
            t = i / RATE
            env = min(1.0, t / ATTACK) * math.exp(-t / TAU)
            # Stop once the tail is inaudible, but not during the attack ramp,
            # where env is legitimately near zero at t = 0 and a plain
            # `env < 1e-4` check ends every note before it starts.
            if t > ATTACK and env < 1e-4:
                break
            samples[at + i] += env * sum(
                a * math.sin(2 * math.pi * hz * (k + 1) * t)
                for k, a in enumerate(HARMONICS))
        at += n

    # Normalise to the measured peak rather than scaling by the sum of the
    # harmonic amplitudes. They do not all peak together, and with overlap the
    # sum is not knowable up front - dividing by the harmonic total landed about
    # 6 dB under the hand-made stem, which is how this was wrong the first time.
    loudest = max((abs(v) for v in samples), default=1.0) or 1.0
    gain = PEAK / loudest

    frames = bytearray()
    for v in samples:
        frames += struct.pack('<h', int(max(-1.0, min(1.0, v * gain)) * 32767))

    return bytes(frames), total / RATE


def write(path, data):
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(data)


def main():
    # Flags are stripped before the positional arguments are read. Left in,
    # `stem.py --force` takes the flag as the input filename and dies on it, and
    # `stem.py in.bin --force` takes it as the output directory.
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    force = '--force' in sys.argv[1:]

    src = args[0] if len(args) > 0 else 'unpacked/vga.bin'
    out = args[1] if len(args) > 1 else 'music/original'
    os.makedirs(out, exist_ok=True)

    song = M.read_song(open(src, 'rb').read())
    order = song['orders'][M.SPEAKER_ORDER]

    for name, start in (('theme-stem.wav', 0), ('jingle-stem.wav', M.JINGLE_FROM)):
        path = os.path.join(out, name)

        # theme-stem.wav in the repository was made by hand before this tool
        # existed, and what comes out here is close but not identical - within
        # 1.7 dB of it overall, with per-second peaks inside about a quarter.
        # Overwriting it by default would quietly swap a used artefact for a
        # lookalike, so an existing file is left alone unless --force says so.
        if os.path.exists(path) and not force:
            print(f'{path}: exists, left alone (pass --force to overwrite)')
            continue

        struck = notes(song, order, start)
        data, seconds = render(struck)
        write(path, data)
        sounding = sum(1 for hz, _ in struck if hz is not None)
        print(f'{path}: {seconds:.3f} s, {sounding} notes, '
              f'from order position {start}')


if __name__ == '__main__':
    main()
