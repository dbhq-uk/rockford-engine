# Where the original puts things

Established by **running** the VGA executable, not by reading it. Every number
here came out of the interpreter in `src/Rockford.Reference`, and the ones that
restate a claim in [`PORT-PLAN.md`](PORT-PLAN.md) say so.

## The load

`ROCKFORD.EXE` is EXEPACK-packed: 31,789 bytes on the VGA disk with a 512-byte
header and **no relocation entries**, unpacking to 84,800. The packer keeps its
own relocation table inside the stub and applies it at run time, so the harness
loads the packed file and lets the game unpack itself.

DOS puts the image at paragraph `0x110` in this harness. That is a choice, not a
fact about the original - a real DOS puts it wherever memory allows - but every
offset below is relative to it and survives the choice.

## The segments

| | Segment | Base, as a file offset |
|---|---|---|
| Data | `LoadSegment` | `0x00000` |
| Code, VGA | `LoadSegment + 0x0E9F` | `0x0E9F0` |
| Code, EGA | `LoadSegment + 0x1CFB` | `0x1CFB0` |

**The code paragraph is not a constant and must not be treated as one.**
EXEPACK unpacks each build to a different base - it is the `real_cs` in the
packed stub, which `tools/unexepack.py` prints. Assuming `0x1000` gave the
right *linear* address for the VGA loader by coincidence, and the wrong `CS`
for every routine in it. That is invisible until something reads `cs:`-relative
data, and then it is a wrong answer rather than a crash. The harness reads the
segment off the running program at its first BIOS or DOS service call instead -
in practice the video BIOS's `INT 10h`, which both builds make before they open
anything.

**A `ds:` offset in the documentation is a file offset into `unpacked/vga.bin`.**
That is why `PORT-PLAN.md` can cite `ds:0x1656` and `0x140DE` in the same
paragraph without converting between them.

Measured rather than assumed: the first `INT 21h` open call passes its filename
at `DS:0x1DC0`, and `TITLE.SCM`'s name is at file offset `0x1DC0`.

The code segment is measured too, and **arithmetic on the disassembly is not
enough to get it**. Reading `call 0x878` at file `0xFE4D` reaching file
`0x10878` and concluding a base of `0x10000` gives the right *linear* address
and the wrong `CS` - the call's own encoding is relative, so it constrains the
difference between two offsets and says nothing about where the segment starts.
The running program says `0FAF`, a base of `0x0E9F0`, and the difference matters
to any routine that reads `cs:`-relative data. The frame pacer does exactly
that.

So a cited address is reached at the recorded code segment with
`IP = address - codeBase`, where `codeBase` is `(codeSegment - loadSegment) * 16`.
In the VGA build that is `0x0E9F0`, so the cave loader at file `0x10878` is
reached at `0FAF:1E88` - which is exactly where the original's own
`call 0x878` at file `0xFE4D` lands.

## The cave grid

The loader at `0x10878` takes **no arguments**. Everything it needs is fixed:

```
10878: movw $0x0,0x1614      ; and 1616, 161c, 161e, 166a, 166c, 1678, 1686, 1696
108a9: cld
108aa: mov  si,0x3010        ; the authored cave record
108af: add  bx,0x370         ; 880 bytes of it
108b3: mov  di,0
108b6: mov  ax,0xffff
108b9: mov  cx,0xb00         ; 2,816 words
108bc: rep  stosw            ; prefill
108c1: xor  cx,cx            ; column
108c3: push di               ; row start
108c4: lodsb                 ; one authored byte
       ...                   ; the if/else chain, first match wins
10a61: inc  cx
10a62: cmp  cx,0x28          ; 40 columns
10a6a: pop  di
10a6b: add  di,0x100         ; row stride
10a6f: cmp  di,0x1600        ; 22 rows, and then stop
10a7e: ret
```

**It needs `ES = DS`, and not for the reason it first appears.** Only the
prefill goes through `ES` - `rep stosw` of 2,816 words. The decoded cells are
written through `DS`, and the authored cave is read through `DS` as well. So
calling it with `ES` pointing elsewhere returns normally having written 881
words into the `DS` grid and prefilled 2,816 words into somebody else's
segment: the grid is decoded but never cleared, which is a quieter kind of
wrong than an untouched one.

Which settles, from the loop bounds rather than from inference:

- **The grid is at `ds:0x0000`.** `di` starts at 0 and the loop ends when it
  reaches `0x1600`, which is 22 rows of `0x100`.
- **40 columns**, from `cmp cx,0x28`.
- **A cell is a word**, value byte then state byte - every arm of the chain
  writes one, e.g. `movw $0xff01,(%di)` for authored `0x01`.
- **The prefill is `0xFFFF` over the whole `0x1600`**, which is what stops
  anything growing into the 88 padding columns of each row.

`CaveGrid` in the port implements this. One difference, and it is outside the
compared region: the original prefills `0xFFFF` over the 22 rows, and
`CaveGrid`'s constructor fills its two *extra* padding rows with `0xFF02`,
steel. The loader overwrites everything inside `0x1600` either way, so the
comparison covers 22 rows and the padding is the port's own arrangement rather
than a claim about the original.

### It needs no cave loaded to drive

Because the loader reads from a fixed address, a comparison can write 880
authored bytes to `ds:0x3010`, call `LoadSegment+0x1000:0x0878`, and read the
grid back - no menu navigation, no file I/O, and no dependence on which cave the
game happens to have selected. `CELLMAPS.BIN` is 35,200 bytes, which is 40
records of 880.

## The clock

The game programs 8253 channel 0 with divisor `0x4DAE`, which at 1.193182 MHz is
**60.0 Hz**. `PORT-PLAN.md` states this from a reading; here the program writes
it. The frame pacer at file `0x1334F` compares against **six**, and waits while
the counter is below it. `0x1D87E` in the EGA build is a different loop with a
different constant; the number in `PORT-PLAN.md` is the simulation's, and the
one quoted here previously was read off the title screen's pacer instead.

**The instruction timing underneath is an approximation** - three PIT ticks an
instruction, and each iteration of a repeated string operation past the first
is charged as two instructions, so six ticks. It is exact and reproducible, and it is not the speed of a real 8088.
Nothing here establishes that the game runs at its original speed, only that it
runs at a fixed one.

It hooks `INT 08h`, `INT 09h`, `INT 23h` and `INT 24h`, and **only** those. The
counter its timer handler increments is the one the pacer spins on; nothing
reads `INT 1Ah`.

### And it checks whether it has already hooked the timer

```
21393: mov  ah,35h
21395: mov  al,08h
21397: int  21h
2139b: cmp  ax,cs:[12h]
213a0: je   213cc            ; skip the install
```

Which is why a harness must present a **non-zero** interrupt table. An empty one
answers zero, matches the program's own zeroed record of what it last installed,
and the game skips installing its timer handler and then waits for it for ever.

## The display

Two releases, two cards, and the port was written against the second.

| | Mode | Layout | Screens |
|---|---|---|---|
| EGA | `0Dh` | four 8,000-byte planes behind one aperture at A000 | `.FIL`, raw, 32,000 bytes |
| VGA | `13h` | linear, one byte a pixel | `.SCM`, run-length encoded |

A 32,000-byte `.FIL` is exactly four planes of 8,000: the file format is the
card. The `.SCM` decoder is the game's own, at `0x1275B`. Its decoding loop executes
seventeen distinct instructions; "nine" was a count of the lines worth quoting,
not of the instructions.

**The routine addresses cited in `PORT-PLAN.md` and in `src/Rockford.Engine`
are VGA file offsets.** `unpacked/ega.bin` holds twelve zero bytes at all six of
the ones `tools/cicodis-check.py` asserts, so a harness that ran only the EGA
build could not compare the documented simulation.

Not *every* address in those documents, though: [`PORT-PLAN.md`](PORT-PLAN.md)
line 581 cites `0x1FAF3` as an EGA routine, and the `JNS` it describes is there
in `unpacked/ega.bin` while the VGA image ends at `0x14B40`. The six-offset
assertion holds; a universal claim does not.
