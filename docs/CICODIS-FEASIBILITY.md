# Can cicodis settle the fidelity claim?

One time-boxed session, 20 September 2026, to answer whether the reference named
in [`PRIOR-ART.md`](PRIOR-ART.md) can answer the question
[`PORT-PLAN.md`](PORT-PLAN.md) leaves open: this engine matches the disassembly
**as read**, and nothing has compared it to a running original.

**The answer is a qualified yes, and the qualification is larger than expected.**
The reference runs the same program this repository disassembled. It cannot be
traced against this port *by address*, because the two are separately compiled
builds of different releases - and one claim in `PRIOR-ART.md` is wrong on that
point and is corrected below.

## What was pinned

| | |
|---|---|
| Repository | `gabonator/Projects` |
| Path | `CicoJit/gamelib/rockford` |
| Commit | `a4809cf629f4e3b0f52e3d1bc0196b788a966d37` |
| Dated | 25 October 2024 |

The directory holds `fetch.sh`, `cico.sh`, `run.sh`, `xcode.sh`, `clean.sh`,
`rockford.patch`, `unpacked.zip`, and a `cicorun/` directory with the generated
`rockford.cpp` - **10,224 lines, as `PRIOR-ART.md` says** - alongside `main.cpp`,
`ega.h`, `sdl.h`, `cicoemu.h` and `controls.h`.

> **Corrected, 20 September 2026.** This said the generated C++ was not
> committed and accused `PRIOR-ART.md` of describing the output as though it
> were the input. Both wrong: I read the GitHub contents listing, saw
> `0  cicorun`, and took a directory's size-0 entry for an empty file. The
> translation is committed and building it needs no recompiler run. Astra
> caught it.

## The executable is ours

`fetch.sh` downloads the 1987 EGA release from `classicreload.com`, then
**deletes the `ROCKFORD.EXE` it just downloaded** and unzips the repository's own
`unpacked.zip` over it. So the binary cicodis translates is the one committed
beside the scripts, not the one fetched.

That binary is 143,040 bytes. Ours is 142,880. Removing the 160-byte MZ header
leaves two files that differ in **twelve bytes**:

```
differ at file offsets 0x1CFA4..0x1CFAF  (12 bytes, contiguous: True)

cicodis : 4B 20 20 20 53 54 41 43 00 00 00 00 00 00 00 00
ours    : 4B 20 20 20 53 54 41 43 4B 20 20 20 53 54 41 43

cicodis ascii: b'\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00'
ours    ascii: b'K   STACK   '
```

It is the tail of the unpacker's repeating `"K   STACK   "` stack filler, zeroed
in one copy and not the other. Uninitialised padding, not code and not data the
program reads.

**So the reference and this repository are looking at the same program.** That
is the single most important thing this session had to establish, and it holds.

## But this port is VGA, and cicodis is EGA

`PORT-PLAN.md` and `FORMATS.md` cite engine addresses throughout - the random
generator at `0x124B1`, the scan loop at `0x140DE`, the player handler at
`0x1356C`, the viewport scroll at `0xFF87`, the dispatch table at `0x13C50`.

Every one of those is a **VGA** file offset, and `unpacked/ega.bin` is zeros at
**all six**:

```
random 0x124B1
  vga: A1 60 22 05 48 92 D1 C8 D1 C8 D1 C8
  ega: 00 00 00 00 00 00 00 00 00 00 00 00

scan 0x140DE
  vga: FC C7 06 04 16 00 00 C6 06 A0 16 FF
  ega: 00 00 00 00 00 00 00 00 00 00 00 00
```

Searching `ega.bin` for those routines' bytes *anywhere* finds **one of six**,
and only up to a point:

```
   8-byte needles: 1 of 6
  16-byte needles: 1 of 6
  17-byte needles: 1 of 6
  18-byte needles: 0 of 6
  48-byte needles: 0 of 6
```

The one match is the player handler's first seventeen bytes, at EGA `0x21A91`.
Seventeen bytes of agreement is a shared prologue, not a shared routine - and
past it the builds diverge. (This said "at any needle length from 8 bytes to 48"
until 20 September 2026, which was carelessly extrapolated from three
measurements and is false above 17.)

The two releases are separately compiled: same game, different code layout,
different data addresses. A single rebase cannot map even the pair that does
match - the random generator would need `+0xE420` and the player handler
`+0xE525`.

`tools/cicodis-check.py` re-derives all of this. Findings about two binaries go
stale without anyone noticing - either file could be replaced - so the document
is not the only record of them:

```
$ python3 tools/cicodis-check.py path/to/cicodis/ROCKFORD.EXE
  ok    all 6 cited offsets are zeros in ega.bin (found 6)
  ok    the only 16-byte match is player handler at 0x21A91
  ok    player handler matches at 17 bytes
  ok    nothing matches at 18 bytes or more
  ok    the bodies are the same length (142880 against 142880)
  ok    they differ at exactly 0x1CFA4..0x1CFAF (12 bytes differing)
  ok    ours reads the stack filler there
  ok    theirs reads zeros there

cicodis: as documented
```

**Every line is asserted, not reported.** The first version of that file printed
the numbers and returned 0 whatever they were, so all three of its findings could
be falsified and it still passed - Astra demonstrated exactly that by mutating
the binaries in memory and watching it exit clean. `--selftest` now reproduces
those three mutations, and a fourth, and requires the check to reject each one;
it also drives the tool's own exit path, because a check whose findings
discriminate and whose exit code does not is the same defect one level up.

It runs without the cicodis executable, checking what it can and still failing
when a finding breaks. The executable is deliberately not committed here: it
belongs to that project, and this repository already holds more of somebody
else's data than it would like.

### The correction this forces

`PRIOR-ART.md` says of cicodis:

> **Addresses map by subtracting `0x10000`.** A `sub_2xxxxx` in that file is our
> unpacked EGA file offset plus `0x10000`; data offsets appear unchanged. So it
> can be read against `FORMATS.md` and `astra-engine-audit.md` directly.

The first sentence is right. **The last one is wrong for code.** A `sub_2xxxxx`
label maps to an offset in `ega.bin`, and `FORMATS.md`'s engine addresses are in
`vga.bin`. The two cannot be lined up by subtracting anything.

It went unnoticed because the one thing cicodis was actually used for - settling
the palette block boundaries at `0x4071`, `0x4082`, `0x4093`, `0x40A4` - is
**data**, and the sentence itself says data offsets appear unchanged. The claim
held for the use it was put to and fails for the use it was written to justify.

## The translation is not faithful where the patch touched it

This is the finding that most changes what the reference is worth, and it was
Astra's rather than mine.

`cico.sh` applies `rockford.patch` to the generated C++, and one of the patched
lines is marked `// gabo` in the treasure routine at `cicorun/rockford.cpp:4862`:

```c
tl = memoryAGet16(ds, 0x3584) == 0; // gabo
memoryASet16(ds, 0x3584, memoryAGet16(ds, 0x3584) - 1);
if (memoryAGet16(ds, 0x3584) != 0)
    goto loc_2faf3;
...
loc_2faf3:
    if (tl)
        return;
    memoryASet16(ds, 0x3584, 0x0000);
```

The original, read out of `unpacked/ega.bin` at the addresses cicodis' own
labels give:

```
0x1FAC6:  FF 0E 84 35          dec  word ptr ds:[0x3584]     ; the quota
          75 27                 jnz  loc_2faf3
0x1FAF3:  79 06                 jns  ret
          C7 06 84 35 00 00     mov  word ptr ds:[0x3584], 0
          C3                    ret
```

`JNS` is a **sign** test. The original clamps the quota to zero only when the
decrement takes it NEGATIVE, and otherwise leaves it alone. The patch stands
`tl` in for the sign flag but computes it as a zero test on the value *before*
the decrement - so the two disagree on the ordinary case:

| | quota 40, one gem taken |
|---|---|
| original | 39 |
| cicodis as patched | **0** |

One gem satisfies the whole quota. That is not a subtle drift in a corner; it is
the game's central rule, in the routine that implements it.

**So "it assumes the translation is faithful" is not an unproven assumption
here. It is a disproven one, at least at this point.** `PRIOR-ART.md` carried
that caveat and an earlier version of this session's work removed it as
discharged by the binary identity. It is not: identical input bytes say nothing
about what a translator and a hand-written patch did with them.

What this costs the plan below: the reference cannot be treated as original-game
truth until `rockford.patch` is audited line by line. Every `// gabo` is a human
decision, and this one is wrong.

## Running it is its own piece of work

`cico.sh` is macOS on Apple silicon, hard-coded:

```sh
arch -arm64 g++ -std=c++17 ../../cicodis/cicodis/main.cpp \
  -I/opt/homebrew/Cellar/capstone/5.0.1/include/ \
  -L/opt/homebrew/Cellar/capstone/5.0.1/lib -lcapstone.5 -o cicodis
```

`arch -arm64` and `/opt/homebrew` are not portable, and `xcode.sh` is the front
end's build. Getting a running reference on this machine means porting the build
before anything else - capstone 5.0.1 and a C++17 compiler are the substance,
the rest is paths.

Not attempted in this session. It is tractable and it is not free, and it is the
wrong thing to spend the next hour on given what the address finding implies.

## The trap the issue names is real, and it is the first one you would hit

`GameHost.Load` builds every cave's simulation with a fixed seed:

```csharp
Sim = new Simulation(loaded.Grid, _tables.Time[n], randomSeed: 0x04D2)
```

while `Simulation.CarryGlobalsFrom` deliberately carries the footstep state, the
timed wall's timestamp and **both worms' headings** across a cave boundary,
because the original's loader leaves those alone.

So this port reseeds per cave and the original carries one continuing stream. A
trace comparison that did not know this would diverge on the first random draw
of the second cave and report an engine defect that is a documented, chosen
difference. The issue is right to put identifying these *before* running
anything.

Worth stating plainly: **that difference alone means an instruction-level trace
comparison cannot be the first experiment.** The two programs are not doing the
same arithmetic after cave one.

**And not only after cave one.** Both binaries initialise the generator word to
`0x04D2`, so the values agree at the start - but the original draws from it
before a cave is ever loaded: the pre-game animation calls it at EGA `0x1D8BC`,
which is visible in the translation too. So the states have already parted by
the time cave one begins, and even that first cave needs the alignment stated
explicitly rather than assumed. (This said the divergence waits for cave two,
which was wrong in the direction that flatters the plan.)

Reseeding does not make a controlled instruction-level experiment impossible. It
makes one that has not accounted for this meaningless.

## What this means for the differential replay

Three routes, and the first is the one to take.

**Compare at the observable level, not the instruction level.** #39 landed a
session recorder: a canonical list of the calls made of the host, replayable
into a fresh engine and proved to reproduce a run exactly. Instrument a cicodis
build to dump its 40 by 22 cave grid once per simulation step, feed both sides
the same input sequence, and compare cell by cell. This sidesteps the address
problem entirely - it never needs the two builds to agree about where anything
lives, only about what the cave looks like - and it is the comparison that
actually tests the claim. The reseed difference is handled by comparing a single
cave from a known load, which is where any such harness would start anyway.

**Re-audit against EGA.** Expensive, and it does not have to be destructive -
parallel EGA mappings could be added beside the VGA citations rather than
replacing them. It is still a large piece of work to gain a reference for a
release this port does not follow. (This said it would "invalidate every address
citation", which overstates it.)

**Find a VGA translation.** None was found. cicodis' Rockford directory is the
EGA release, and the searches recorded in `PRIOR-ART.md` on 15 September turned
up nothing else - which is not the same as one not existing, and this said
"there is not one" as though it were.

## Verdict

The reference **might** answer the question, and it cannot answer it yet.

What holds: the binary is the right one, established to twelve bytes of inert
stack padding.

What does not: the addresses are not comparable with this port's, so the route
`PRIOR-ART.md` implies is struck. The build is macOS-only and needs porting. The
random state has already parted before cave one. And the translation is
**demonstrably wrong** in the treasure routine, so nothing it does can be
treated as original-game truth until `rockford.patch` has been read line by
line.

"This reference cannot answer our question" was a permitted outcome and it is
still not quite the outcome - but it is closer than the first draft of this
document said. The honest position is that the reference is promising, is not an
oracle, and needs a patch audit before anybody builds a harness on top of it.
The comparison to build, once it is trusted, is at the cave grid rather than the
instruction.
