# Rockford file formats

Reverse engineered from the 1988 MS-DOS release of *Rockford: The Arcade Game*, the Boulder Dash spin-off published by Mastertronic and First Star Software.

Three releases were examined, all from the Internet Archive. The release number is the build date from the `darkstar/tdc-database` listing, which carries a size and CRC32 for every file of every release; each copy here was identified by matching those.

| Release | Here | Disks | Graphics files | Worlds on the menu |
|---|---|---|---|---|
| EGA r871109, 9 Nov 1987 | `original/ega1987`, `extracted/ega1987-1`, `-2`, `unpacked/ega1987.bin` | 2 x 5.25" 360K | `.FIL`, `.CAR` | hunter, scuba, cook, player, cowboy |
| EGA r880129, 29 Jan 1988 | `original/ega`, `extracted/ega1`, `ega2`, `unpacked/ega.bin` | 2 x 5.25" 360K | `.FIL`, `.CAR` | hunter, cook, cowboy, space, body |
| VGA r880225, 25 Feb 1988 | `original/vga`, `extracted/vga`, `unpacked/vga.bin` | 1 x 3.5" 720K | `.SCM`, `.CCM` | hunter, cook, cowboy, space, body |

The two 1988 releases ship the same `CELLMAPS.BIN` and a near-identical `ROCKFORD.EXE`.

### The 1987 release, and the two worlds it shipped

**Confirmed.** The first release shipped scuba and player, which 1988 withdrew. Its files, by CRC32, against the tdc-database listing for 6508.6:

| File | Bytes | CRC32 | Also in |
|---|---|---|---|
| `ROCKFORD.EXE` | 29,491 | `0xad786129` | no other release |
| `CELLMAPS.BIN` | 17,600 | `0x7ebb799b` | no other release |
| `SCUBA.FIL`, `SCUBA.CAR` | 32,000 each | `0x7af0a3e5`, `0x69efc3ad` | no other release |
| `PLAYER.FIL`, `PLAYER.CAR` | 32,000 each | `0x6e1320e5`, `0xe457418b` | no other release |
| `HUNTER`, `COOK`, `COWBOY` `.FIL` and `.CAR`, `TITLE.FIL`, `TITLEANI.FIL`, `AUTOEXEC.BAT` | | | identical in r880129 |

**Looking at the sheets.** They are planar EGA and the world palettes are the 1988 executable's, so they need the palette-aware decoder rather than the default EGA one:

```
python3 tools/egapal.py extracted/ega1987-2/SCUBA.FIL scuba render/ega1987/scuba-fil.png
```

`render/ega1987/` holds that output for scuba, player and hunter, sheets and cartoons - the only pictures of two worlds' artwork that exist, so they are committed rather than left to be regenerated.

The archive.org zip's MD5, `360258d72887781f3bde3f00d4db1540`, matches the item's own `_files.xml`. It was uploaded on 13 August 2021 by the same account as the 1988 EGA item, 96 seconds apart. (`msdos_Rockford_-_The_Arcade_Game_1987`, also on archive.org, is labelled 1987 but is r880129.)

**The caves.** The 1987 `CELLMAPS.BIN` is twenty records, and they are byte for byte records 0-19 of the 1988 file: hunter, scuba, cook, player and cowboy, raw worlds 0 to 4, contiguous. The 1988 file keeps them untouched and appends raw worlds 5 to 9. So the alternating layout described under `CELLMAPS.BIN` below is not a hiding pattern. It is a five-world file with five more worlds added after it, and a menu that picks every other one.

**The menu.** The 1988 EGA executable carries a ten-word table at file offset `0x46CA`, `0, 2, 4, 6, 8, 1, 3, 5, 7, 9`, the menu-to-raw-world map. **Confirmed: the 1987 executable does not have it.** The bytes on either side of the table in 1988 are contiguous in 1987, and the offsets measure it. Data just before the table sits 62 bytes earlier in 1987 (`0x46BE` in 1988 is `0x4680` in 1987), and data just after it sits 82 bytes earlier (`0x46E0` is `0x465E`). The difference is 20 bytes, the table's own size.

**Confirmed from the code, and the two releases do not merely differ by a table - they select a world in completely different ways.**

1987 types it. The routine at `0x20134` in `unpacked/ega1987.bin` reads a number, rejects 0 and anything above 5, decrements, and stores the result as the raw world with nothing in between:

```asm
20134: cmp ax,0x0
20137: jg  0x2013a              ; reject 0
2013a: cmp ax,0x5
2013d: ja  0x2015b              ; reject above 5
2013f: dec ax                   ; 1-5 becomes 0-4
20140: mov ds:0x46a2,ax         ; the raw world, directly
20143: add WORD PTR ds:0x46a2,0x0
```

That is the live path for the string `Enter world 1-5`, which both executables carry and only this one uses.

1988 points at it. The world-select screen is a picture with five boxes, and the routine at `0x1EBC1` in `unpacked/ega.bin` takes the highlighted entry and maps it:

```asm
1ebc1: mov si,ds:0x35ea         ; which box is lit
1ebc5: mov ds:0x3ce0,si
1ebc9: add si,0x0
1ebcc: shl si,1
1ebce: mov ax,[si+0x46ca]       ; 0,2,4,6,8,1,3,5,7,9
1ebd2: mov ds:0x46f4,ax         ; the raw world
```

**There is no `MENU.FIL` on either 1987 disk.** The graphical select screen, its five portraits and the map table all arrive together in 1988, and the typed prompt they replaced could only ever reach worlds 0 to 4 - which is exactly the five the 1987 release shipped.

*Inferred:* the redundant `add 0` in both routines is a patch point. Set the 1987 one to 5 and the typed prompt selects raw worlds 5 to 9, which is what a second disk of worlds would have needed. It is a one-byte change in both builds and neither uses it.

**The art.** `SCUBA.FIL` and `PLAYER.FIL` are complete sheets in the standard 20 by 12 layout, and render through `tools/egapal.py` with the scuba and player palettes. `SCUBA.CAR` and `PLAYER.CAR` are complete cartoons. They are the only surviving first-party art for any of the five worlds the 1988 menu cannot reach.

**The executable** unpacks with `tools/unexepack.py` to a real entry of `1CF6:02F3`. It already names the `.FIL` and `.CAR` files of all ten worlds and carries all ten world palettes, identical to 1988's and starting at `0x4068`. All ten worlds were planned before the first release went out; music, luck and miner were never finished in any release.

Confidence is marked throughout:

- **Confirmed** - proven from the shipped data, and stated how.
- **Inferred** - the only reading consistent with the data, but not directly proven.
- **Unknown** - not yet identified. Listed rather than guessed at.

## Getting the files off a disk image

The images are raw FAT12 floppies. `tools/fat12.py` extracts them without needing `mtools` or a loop mount:

```
python3 tools/fat12.py DISK01.IMG extracted/
```

Disk contents:

```
AUTOEXEC.BAT    ROCKFORD.EXE    CELLMAPS.BIN
TITLE.*  TITLEANI.*  MENU.*                  # front end
HUNTER.*  COOK.*  COWBOY.*  SPACE.*  BODY.*  # one set per world
```

## CELLMAPS.BIN - the caves

**Confirmed.** This is the whole level set and it is as plain as a format gets.

```
35,200 bytes = 40 caves x 880 cells
880 cells    = 40 wide x 22 high, row-major
1 byte       = 1 cell
```

No header, no compression, no per-cave metadata. The 40 caves are **10 worlds of 4**,
and the five the 1988 menu reaches are interleaved with five it does not:

| Raw world | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 |
|---|---|---|---|---|---|---|---|---|---|---|
| World | hunter | scuba | cook | player | cowboy | music | space | luck | body | miner |
| Caves | 0-3 | 4-7 | 8-11 | 12-15 | 16-19 | 20-23 | 24-27 | 28-31 | 32-35 | 36-39 |
| Treasure | gold | gems | apples | cups | coins | notes | suns | clubs | hearts | jewels |
| 1987 | yes | yes | yes | yes | yes | - | - | - | - | - |
| 1988 | yes | dropped | yes | dropped | yes | caves only | yes | caves only | yes | caves only |

**The interleave is not a hiding pattern, and reading it as one was wrong.** The
1987 release's cave file is twenty records and they are byte for byte records
0-19 of this one; 1988 kept them, appended raw worlds 5 to 9, and pointed the
menu at every other world. The alternation is the arithmetic of appending five
worlds to five and choosing again, not a decision to conceal anything. The
evidence is in the release section above rather than repeated here.

That splits the five worlds the 1988 menu cannot reach in two, and they are not
the same kind of thing:

- **scuba and player were withdrawn.** They shipped, and their artwork survives
  in r871109 - `SCUBA.FIL` CRC `0x7af0a3e5`, `PLAYER.FIL` CRC `0x6e1320e5`.
- **music, luck and miner were never released.** No release carries a tile for
  them, the Amiga disk included (`PRIOR-ART.md`, "Searched, and there is nothing
  else"). A filename, a treasure name, a palette and four caves each is all any
  release gives them. The Amiga's palettes for them are on its disk: see "The
  Amiga disks" in `PRIOR-ART.md`. Luck was drawn once, on the Amiga: a screenshot of it is on
  the back of the Atari ST box.

The faithful build draws all five with Aleksi Sahala's reconstructions, not with
any of the above - see `third_party/rockford-patcher/README.md`.

**One cave was reused when player was dropped.** Record 12, player's first cave,
and record 34, body's third, share **81%** of their interior walls; no two of the
1988 caves share more than 42%, and typically about 9%. Player's shipped in 1987,
before body existed, so body's third cave is player's first, carried over when the
world it belonged to came out of the menu. It is the only one: every other scuba and
player cave is its own design, and none shares more than 36% of its walls with a
cave from another world.

So the reachable caves are 0-3, 8-11, 16-19, 24-27 and 32-35. This follows from the
index at `0xFE9D`, `raw world * 4 + stage`, and the menu's five entries mapping to
raw worlds 0, 2, 4, 6 and 8 - see the metadata item below. The prior art reached the
same order independently, and its patch for playing the hidden caves is simply to
delete the first four caves so that every hidden world shifts into a playable slot:
[`PRIOR-ART.md`](PRIOR-ART.md).

**The world order in that table is the executable's own**, not a reconstruction. The
tileset filename table at `ds:0x267A` is ten word pointers to `SPACE.SCM`, `COOK.SCM`
and the rest, indexed by the raw world in `ds:0x2C92`:

```asm
129d7: mov si,WORD PTR ds:0x2c92
129db: mov bx,0x267a
129de: shl si,1
129e0: mov dx,WORD PTR [bx+si]     ; "HUNTER.SCM", "SCUBA.SCM", "COOK.SCM", ...
```

Note `player` at 3 and `luck` at 7, which is the pair the prior art had the other way
round.

### The treasure names, and which world gets which

**Confirmed.** Ten names sit at `ds:0x1C81` in **fixed 8-byte fields**, right-aligned
and null-padded - `"  GOLD\0\0"`, `"  GEMS\0\0"`, `"APPLES\0\0"` and so on. The HUD
indexes them by the same raw world:

```asm
fee7: mov si,0x1c81
feea: mov ax,ds:0x2c92
feed: shl ax,1
feef: shl ax,1
fef1: shl ax,1                     ; raw world * 8
fef3: add si,ax
```

That gives the treasure row in the table above, and it settles a question the prior
art states as open: *"No-one knows how the collectibles mentioned in ROCKFORD.EXE
should be connected with the missing themes."* They are connected by position, and
the answer is in the indexing rather than in the order the strings happen to sit in.

What proves the layout rather than merely fitting it:

- 35,200 divides exactly by 880, with no remainder for a header.
- Reading at 40x22, every cave has an unbroken `0x02` border on all four sides.
- `0x74` occurs **exactly once in every one of the 40 caves**.
- `0x20` occurs **exactly once in every one of the 40 caves**.

A wrong stride would not produce all four of those at once.

### Cell values

Only 25 distinct byte values appear across all 40 caves.

| Value | Meaning | Count | Confidence |
|---|---|---|---|
| `0x00` | Space | 6,544 | Confirmed |
| `0x01` | Dirt | 13,508 | Confirmed - the commonest value, as the diggable fill must be |
| `0x02` | Steel wall | 7,263 | Confirmed - forms every cave border |
| `0x03` | Brick wall | 2,738 | Confirmed - authored as interior runs |
| `0x04` | **Magic wall** (timed converting wall) | 96 | Corrected - loader `0x10904`, conversion handler `0x13F57` |
| `0x08` | **Expanding wall** | 163 | Corrected - loader `0x10912` |
| `0x0C` | **Quicksand** - pass-through behaviour, runtime `FF04` | 64 | Corrected - loader `0x10920`; not a magic wall. Named by the prior art |
| `0x10` | **Amoeba** | 28 | Corrected - loader `0x1092E` gives runtime `FF48` |
| `0x20` | **Rockford start** | 40 | Corrected - loader `0x1093A`, two-cell sprite, position stored twice |
| `0x28` | Boulder | 2,126 | Confirmed |
| `0x2C`-`0x2F` | Flying-enemy family, with `0x30`. **Yields treasure** | 28 | Corrected - not a separate worm family |
| `0x30`-`0x33` | Enemy, second family | 97 | Inferred |
| `0x34`-`0x37` | Enemy, primary family. **Yields space** | 412 | Confirmed |
| `0x74` | **Exit** | 40 | Corrected - loader `0x10A02` gives runtime `FF07`, quota-gated at `0x14316` |
| `0x84` | **Good worm** - second timed entity, tracked at `DS:161C` | 8 | Corrected - loader `0x10A33`; not the amoeba. Named by the prior art |
| `0x88` | Treasure | 2,017 | Confirmed |
| `0x38`, `0x70`, `0x78`, `0x7C`, `0xC4` | **Fire tap, evil worm, treasure bonus, time bonus, acid tap** | 7, 3, 2, 9, 7 | Corrected - loader `0x109E9` onwards. Shapes from the code, names from the prior art. The counts said "9-4 each" until 13 October 2026; a byte count of `CELLMAPS.BIN` gives these |

**This table was substantially wrong until 15 September 2026 and the corrections
above come from the loader itself.** Every "Corrected" row is sourced to a loader
instruction in `astra-engine-audit.md` (the independent audit of the first engine, kept private), which supersedes
this section wherever the two disagree. The headline is that Rockford and the
exit were the wrong way round, so the engine starts him standing on the exit.

### Directional entities

The low two bits of a moving entity's value are its facing: `0 = up, 1 = right, 2 = down, 3 = left`.

This is why `0x34`, `0x35`, `0x36` and `0x37` are one entity in four rotations rather than four entities. The evidence is that they never co-occur in a way that suggests distinct roles, and they fall into exactly the groups-of-four you would expect. `0x30`-`0x33` and `0x2C`-`0x2F` behave the same way.

### Two results that came out of the level design, and how they went wrong

**Both conclusions below are withdrawn.** The reasoning was sound and the
identifications were not, because both rest on the cell-value table above, which
was wrong. Kept here rather than deleted, because how a plausible inference fails
is worth more than the inference was.

The common fault: a census of what a cave contains only constrains the answer if
you already know what the bytes mean. Neither result came from disassembly, and
that is exactly why neither held.

**Withdrawn: "`0x10` is a magic wall."** Cave 21 contains 188 boulders, one exit, one Rockford - and no treasure, no enemies and no amoeba. Nothing in it can produce a collectable. Its layout is five stacks of boulders, each sitting on a run of `0x10`:

```
#..OOOO...OOOO...OOOO...OOOO...OOOO....#
#..OOOOhhhOOOOhhhOOOOhhhOOOOhhhOOOO....#     h = 0x10
#..OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO....#
```

The only way to finish cave 21 is for `0x10` to convert boulders into treasure. That is what a magic wall does.

> **Why it fails.** The loader at `0x1092E` turns authored `0x10` into runtime
> `FF48`, an amoeba. The cave-21 argument still stands as far as *"the twelve
> `0x10` cells are the only route to a collectable in this cave"*, which is a
> real constraint. It just does not follow that the entity is a magic wall. An
> amoeba that yields diamonds once you wall it in fits those five boulder stacks
> better than a magic wall ever did.

**Withdrawn: "both enemy families explode into treasure."** Seven caves ship with no treasure tile at all. Listing each one's possible routes to a collectable settles which enemy yields what:

| Cave | Routes available |
|---|---|
| 5 | amoeba, `0x30` x3, `0x34` x1 |
| 11 | magic wall, `0x30` x26, `0x34` x3 |
| 19 | amoeba, `0x34` x7 |
| 21 | magic wall only |
| 26 | `0x30` x30 (its one magic wall cell has only dirt above it) |
| **29** | **`0x34` x10 - nothing else at all** |
| 37 | amoeba, `0x34` x2 |

Cave 29 has no treasure, no amoeba, no magic wall and no `0x30`. If the `0x34` family exploded into empty space, cave 29 could never be completed. So it yields treasure - **confirmed**. Cave 26 then forces the same conclusion for `0x30`, slightly less tightly, since its single magic wall cell is unusable.

> **Why it fails.** "Cave 29 has no magic wall" was the load-bearing claim, and it
> is false. The magic wall is authored `0x04`, per the conversion handler at
> `0x13F57` where a resting boulder over runtime `74`-`77` produces falling
> treasure. Cave 29 holds **13** `0x04` cells and **38** boulders, so dropping
> boulders through the wall is a second route to treasure and the argument does
> not close. The table above compounds it: the "magic wall" it credits to caves
> 11 and 26 is `0x0C` and `0x10`, neither of which is one, and the "amoeba" it
> credits to caves 5, 19 and 37 is `0x84`, which is not one either. Every row is
> drawn in the wrong currency.
>
> What the enemies actually explode into is settled in
> `astra-engine-audit.md` (the independent audit of the first engine, kept private) from the code, which is where
> it should have been settled in the first place.

The help text and the published world descriptions, in which both enemy types are butterfly-like, were read as support for this. They are consistent with it, but consistency is not evidence, and leaning on them is part of how the wrong answer survived as long as it did.

**Settled from outside as well.** A study of this game from 2006, found on 15
September 2026 and described in [`PRIOR-ART.md`](PRIOR-ART.md), splits the two
families the same way the disassembly does - `0x2C`-`0x30` yield treasure,
`0x34`-`0x37` do not - from playing the game rather than from reading it. Two
independent methods agreeing against the census is as close to settled as this gets.

## The graphics

### EGA - `.FIL` and `.CAR`

**Confirmed.** Every file is exactly 32,000 bytes:

```
32,000 bytes = 320 x 200 at 4 bits per pixel
             = 4 sequential bitplanes of 8,000 bytes
```

Each plane is 1 bit per pixel, most significant bit leftmost - a straight dump of EGA video memory, not a packed format. `src/Rockford.Formats/EgaImage.cs` decodes it; `tools/ega.py` renders a PNG.

`.FIL` holds a world's tile and sprite sheet, addressed as a 20x12 grid of 16x16 tiles. `.CAR` holds a world's cartoons: a 6 by 4 grid of 48 by 48 frames, four animations of six frames, which the game plays from video memory in its main loop - three of the four, see below.

**What plays, and what never does.** *Confirmed in the 1988 EGA executable, `unpacked/ega.bin`, and read the same way in the 1987 one.* The loader reads each `.CAR` into video memory at `A000:4000`, off screen, with the `.FIL` at `A000:2000`, and does it for every world (`0x20ef3`, the table at `ds:0x463e`). One routine at `0x1e045` plays them: it copies six frames in turn, 48 rows of 6 bytes each, from `0x4000` plus an entry of a six-word table, and it is called from the main game loop (`0x1d3e1`, `0x1d3f4`, `0x1d45a`). It chooses between exactly three tables, at `ds:0x4576`, `0x4582` and `0x458e`. They start at source offsets `0x780`, `0xF00` and `0x1680`, which are 48, 96 and 144 rows of 40 bytes: **rows 2, 3 and 4 of the sheet. Row 1, at offset 0, is never read.** Nothing else in the code takes a source from `0x4000`, and the routine for the other display mode (`0x1e293`, tables at `ds:0x459a` and `0x45b2`, source segment `0x14db`) has the same three rows. The 1987 executable has the same three tables and no fourth. On the Amiga disk the same sheet has its rows labelled `NEW`, `WIN`, `LOSE` and `TIME` (`PRIOR-ART.md`, "The Amiga disks"), so the row the PC never plays is `NEW`, the introduction, which is what Aleksi Sahala says: the intro animations are in the files and are not shown. *Inferred:* that rows 2 to 4 are `WIN`, `LOSE` and `TIME` in that order; the next paragraph shows the code agrees.

**When each cartoon plays.** *Read from the EGA disassembly on 11 October 2026, not run.* `ds:0x4160` is the clock in seconds, set at the start of a cave to the cave's time limit plus one (`0x1E3E4`); `ds:0x3584` is the treasure quota still to collect; `ds:0x35A6` is set to `0xFF` when Rockford walks into the open exit (four places, `0x1E631`, `0x1E7A5`, `0x1E923`, `0x1EA5D`); `ds:0x3582` is the death countdown. The table is chosen at `0x1E075`:

| Cartoon | Row | Chosen when | Played from |
|---|---|---|---|
| `WIN` | 2, `y = 48`, table `ds:0x4576` | `ds:0x35DA` is `0xFF`: the exit was entered with the quota met and the cave not ended by a key. Set at `0x1D3CD` | `0x1D3E1` or `0x1D3F4`, **before** the time bonus counts down |
| `LOSE` | 3, `y = 96`, table `ds:0x4582` | Rockford died with the clock still above zero | `0x1D45A`, when the death countdown reaches zero |
| `TIME` | 4, `y = 144`, table `ds:0x458E` | Rockford died with the clock at zero or below | the same call |

Every death sets the countdown to 8 at `0x1F90F`, whether a hazard killed Rockford or the player gave up with `R`. The clock routine at `0x216EC` takes a second off `ds:0x4160` every 60 ticks and, when it goes below zero, kills Rockford the same way (`0x21718`), which is why running out of time lands in the `TIME` row. After the cartoon the game takes a life (`0x1D45D`).

**How long a cartoon plays.** The six frames are shown in turn, each for 9 ticks of the 60 Hz timer, which is 150 ms, and looped. It plays for at least 60 ticks, 1 second, and is then ended by any key or a joystick fire (`0x1FBDA`), or at 360 ticks, 6 seconds, whichever is first (`0x1E118` to `0x1E12E`). The tick counter is `ds:0x35E2`, added to by the timer interrupt (`0x218A5`) and cleared when the cartoon starts (`0x1E0A8`).

**The faithful build plays them** (#137): `Cartoons.For` picks the row from the host's message and the clock, `cartoon.js` holds the timing and the placement, and `game.js` draws it over the frozen cave. Placement is the original's clamps, centred on Rockford; the vertical position in the original's own formula (`0x1E222`) was not worked out, so the centring is an approximation. The port credits the time bonus when the cave completes, so the WIN cartoon plays after it, where the original plays it before. The five hidden worlds play none. VIP plays the same three redrawn at eight to one, from `<world>-cartoon-x8.png`, by the same rules (`tools/vipcartoons.py`, `docs/ROCKFORD-VIP.md`, "The cartoons").

**The VGA executable** sets the same flags in the same places: the win flag at `0xEDFB`, cleared at cave start at `0xFED2`, the exit flag at six sites from `0x100FC`, and the end-of-cave byte at `0x11D85`. Its own cartoon routine has not been read, so its timings are taken to be the EGA's and are not confirmed.

**The debug `N` skips a cave without a cartoon.** It is one of the debug keys read inside the pause loop, behind the chord and the shift key ("The debug keyboard", below), so normal play never reaches it. In the EGA image it sets the exit flag and the end-of-cave byte together (scan code `0x31`, `0x2184D`), which makes the win test above fail. `L` (`0x26`, `0x2183D`) adds a life.

**The palette is solved, as of 15 September 2026.** A palette is a **17-byte block -
16 palette registers and the overscan byte** - handed to `INT 10h, AX=1002h` in
`ES:DX`. The blocks are contiguous in the unpacked EGA executable:

| Offset | What |
|---|---|
| `0x04060` | the title screen again, three bytes different |
| `0x04071` | all black, the fade |
| `0x04082` | `00 01 ... 07 10 ... 17`, the CGA-compatible default set |
| `0x04093` | the title screen |
| `0x040A4` | **hunter, then + 17 per raw world**, to miner at `0x0413D` |

The ten worlds are in raw world order, the same order the cave and metadata tables
use: hunter, scuba, cook, player, cowboy, music, space, luck, body, miner.

**The block boundaries are the game's own**, from the selector that picks one and
calls the BIOS:

```
al = 0 -> dx = 0x4071      al = 2 -> dx = 0x4093
al = 1 -> dx = 0x4082      al = 3 -> dx = 0x40A4
                           ax = 0x1002 ; es = ds ; int 0x10
```

That is what fixes the stride at 17 and proves the trailing `00` is the overscan
register rather than a separator. It is also what places the first world at `0x40A4`
rather than at the start of the run. Taken from the cicodis decompilation of the EGA
executable, cross-checked against the bytes here - see [`PRIOR-ART.md`](PRIOR-ART.md).

**The assignment is verified independently of that.** With plane order `(0,1,2,3)`,
**each of the five shipped worlds picks its own palette out of the fourteen blocks,
5 out of 5.** The test decodes a world's `.FIL` and its VGA `.SCM`, maps each EGA
index to the VGA colour it most often coincides with, and ranks every candidate
against that mapping. `TITLE.FIL` and `TITLEANI.FIL` pick one of the two title
blocks, which differ in three bytes; the test cannot say which, and it does not
matter. The earlier report here of "green where the VGA has purple" was a wrong
assignment, not a wrong table: cook was being coloured with scuba's palette.

Read the bytes as IRGB - bit 4 intensity, bits 2-0 red, green, blue. That beats the
6-bit `rgbRGB` reading on four of the five worlds, and while that margin is small on
its own, Camoto names the image format `img-ega-raw-planar-bgri-fullscreen`, which is
the same plane order and the same colour encoding reached independently. See
[`PRIOR-ART.md`](PRIOR-ART.md).

The world order came from the prior art; the verification and the block boundaries
are from the code. [`PRIOR-ART.md`](PRIOR-ART.md) has both. `tools/egapal.py` reads
the table.

This still matters less than it did: the VGA release carries its own palettes and has
32 colours to EGA's 16, so **VGA is the reference for colour**. What it buys is the
five never-released worlds, which now have a palette as well as a name.

### VGA - `.SCM` and `.CCM`

**Confirmed, and this is the better source of the two releases.** 320x200, one byte per pixel, with a 32-colour palette carried in the file itself.

The container is:

```
[RLE image data, filling the 64,000 pixels of the screen]
[80]                            terminator - a single 0x80, with no value byte after it
[192 bytes]                     32 RGB triples, 16-bit little-endian words, each 0-15
```

The RLE is byte-oriented. The game's decoder is at `0x1275B`, and `tools/scmformat.py` follows it:

```
0x80      ->  end of the stream
0x00      ->  nothing (allowed, and no shipped file uses it)
n & 0x80  ->  run of (n & 0x7F) copies of the next byte
otherwise ->  n literal bytes follow
```

The game does not count pixels; it decodes until the `0x80`. So what comes just before the terminator differs by file. Most `.SCM` files end `01 1A 80`: one literal pixel, `0x1A`, which lands one past the screen and is harmless. `COWBOY.SCM` ends `81 1A 80`, the same pixel written as a run of one. `COOK.SCM`, `MENU_I.SCM` and `TITLEANI.SCM` end on the 64,000th pixel and have a bare `80`. Every `.CCM` ends `01 1A 80`. (Corrected 13 October 2026: this said every file ends `01 1A 80`, read as one literal `0x1A` and then a zero-length run. There is no zero-length run. `0x80` is the terminator, which is why a run of zero cannot be encoded, and reading it as a run swallows the first byte of the palette.)

Decoding every file fills exactly the 64,000 pixels of the screen with values in 0-31, and the trailing palettes are self-evidently right - `TITLE.SCM` ends in a grey ramp for the rock, `HUNTER.SCM` carries jungle greens, `SPACE.SCM` blues.

`.SCM` is the tile and sprite sheet for a world (or the title/menu screen). `.CCM` is the cartoon sheet, the counterpart of the EGA `.CAR` (see above for what the EGA game plays from it; the VGA executable is not checked). A `.CCM` has the terminator but **no palette** - it reuses its world's `.SCM` palette.

**The colours are therefore solved with no guesswork**: they come from the game's own data, not from a reconstruction. `tools/scm.py` decodes it.

### The ten placeholder players

**Confirmed, and it is the only 1988 artwork the never-released worlds have.**
Every shipped sheet carries ten 16 by 32 figures at `x = 0, 16, ... 144`,
`y = 144`. The game's own player animation is the twenty poses at `y = 48`, so
these are not it. Ten figures against ten worlds, and among them a diver in
goggles, a bearded man in a studded helmet, a conductor in tails and a figure
with a clover on his cap. `tools/placeholders.py` writes them out and prints
everything below.

**Nothing in either executable points at them.** A sprite table entry is a planar
byte offset, `y * 40 + b`, so the band's origins are the words `5760, 5762, ...
5778`. Neither unpacked image holds a run of them. The only table that reaches
row 144 covers `x = 192` upwards, which is art the game does draw. Unreachable
by the shipped code is what makes them placeholders rather than sprites.

**Five of the ten are settled from the data.** Each sheet's own player at
`y = 48` is matched against all ten figures by silhouette overlap and exact pixel
agreement, and in all five sheets the best match is that sheet's own world:

| Figure | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 |
|---|---|---|---|---|---|---|---|---|---|---|
| World | scuba? | **hunter** | **cook** | **cowboy** | player? | music? | **space** | miner? | luck? | **body** |
| Evidence | goggles | matched | matched | matched | football helmet | tailcoat | matched | cap lamp | gambler's hat | matched |

So the band is **not** in raw world order, and it is not alphabetical either.
The five matched positions are 1, 2, 3, 6 and 9, which leaves 0, 4, 5, 7 and 8
holding scuba, player, music, luck and miner in an order nothing in the cave or
sprite data fixes. Those five are **Inferred** from the drawings.

**Figures 4, 7 and 8 were read wrongly here on 25 September 2026 and are
corrected above.** The first reading called 4 a miner - a studded helmet over a
full beard - and put luck at 7 for the clover on its cap and the sportsman at 8
by elimination. The studs are the padding of an **American football helmet** and
the beard is its face guard, which is what a world collecting CUPS would wear;
the thing on 7's cap is a **miner's lamp**, not a clover; and 8 is a gambler in
a wide-brimmed hat. Two independent things say so. The Amiga release's own `RF`
file lists all ten worlds with their collectibles - hunter gold, scuba gems,
cook apples, **player cups**, cowboy coins, conductor notes, astronaut suns,
**gambler clubs**, doctor hearts, miner diamonds - which both confirms the
treasure table derived here from `ds:0x1C81` and names the eighth world a
gambler rather than a leprechaun. And the prior art, which reached the same
order from the pictures alone, now agrees with this file at all ten positions
rather than seven. See [`PRIOR-ART.md`](PRIOR-ART.md).

Two of the matches are comfortable and three are not. Space wins by 1.35 against
1.13 for the runner-up; hunter wins by 1.27 against 1.23, which is thin enough
that it would not stand alone. What makes the set trustworthy is that all five
land on a different figure and no figure is claimed twice.

**The colours are per sheet, not per world.** The same figure differs between two
sheets by 41 to 47 per cent of its colour indices while its silhouette differs by
about 3 per cent. Every sheet holds its own coloured copy of the whole cast,
which means there is no copy of a hidden world's figure in that world's own
palette waiting to be revealed. Applying a world's palette to a figure taken out
of another world's sheet is a recolour, not a recovery.

**`SPACE.FIL` is the exception**, exactly as the prior art reports: the EGA space
sheet has two non-zero pixels in the band where the other EGA sheets have about
2,490. The VGA `SPACE.SCM` carries the full ten.

## ROCKFORD.EXE

16-bit real mode, **EXEPACK-compressed, and now unpacked** - `tools/unexepack.py`.

| Release | Packed | Unpacked | Real entry |
|---|---|---|---|
| VGA | 31,277 | 84,800 | `0E9F:02F7` |
| EGA | 29,451 | 142,880 | `1CFB:02F5` |

Both use the 18-byte header variant with the `RB` signature. The unpack is self-validating: the RLE stream is consumed to exactly offset -1 while the output fills exactly `dest_len`, on both files. A wrong decoder does not land on both bounds at once.

Readable strings survive compression and give up the complete rule set:

> Collect enough treasures **to open exit door.**
> Flying and falling objects **will kill you.**
> Drop things on flying enemies **to kill them.**
> Use fire button (space bar) to **pick up treasures, push things over, and blow up dirt.**
> Numeric keypad to move, Space to fire

That last pair is the important one. Rockford has a **ranged action** - fire acts on the adjacent cell in the facing direction without moving him. The engine implements it as `CaveSimulation.Fire`. (This said Boulder Dash has no equivalent and that the difference makes this an arcade game. Corrected 12 October 2026: Boulder Dash's snap, fire with a direction, also takes or digs an adjacent cell without moving. What fire does here beyond that, pushing and blowing up dirt, is in the help text above.)

Other strings name ten worlds - `BODY`, `COOK`, `COWBOY`, `HUNTER`, `LUCK`, `MINER`, `MUSIC`, `PLAYER`, `SCUBA`, `SPACE` - and ten treasure names - `APPLES`, `CLUBS`, `COINS`, `CUPS`, `GEMS`, `GOLD`, `HEARTS`, `JEWELS`, `NOTES`, `SUNS`. The examined release ships 5 of each and prompts `Enter world 1-5`, so the executable is shared across releases.

Credits, from the same strings: `Copyright 1987 by First Star Software, Inc.`, `MS-DOS (TM) version by SYNERGISTIC SOFTWARE, INC.`, `Programmed by John Conley`, `Graphics by David Schroeder, Walter Meyers, and David Johnson`, `Game puzzles by Fernando Herrera` `and Peter Liepa`, `Music by Nick Scarim`, `Adapted from Amiga version` `by Fernando Herrera, Tony Manso, and` `Todd Weiss (assistance)`, `ROCKFORD is based on BOULDER DASH by Peter Liepa with Chris Gray`.

### What it was written in

**Confirmed: hand-written 16-bit x86 assembly, assembled with MASM.** Not C, not
Pascal. Four independent checks agree, and each one on its own would be hard to
explain away.

**There is not one compiler stack frame in either executable.** A scan of both
unpacked images for `55 8B EC` (`push bp` / `mov bp,sp`) returns **zero** hits
across 84,800 and 142,880 bytes. Every DOS C and Pascal compiler of the period
opens a non-trivial function that way. Zero is not an optimised build; it is a
different kind of program.

**There is no BP-relative addressing at all**, so there are no stack locals and no
stack parameters anywhere in the image. Variables are absolute `ds:0xNNNN`
globals throughout.

**Arguments are passed in registers.** The entry point sets up calls like
`mov al,0Fh` / `mov bl,07h` / `call`, and callees read them straight out of `ax`
and `bx`. No DOS C compiler in 1987 did this; `cdecl` put every argument on the
stack, and Borland's `__fastcall` did not exist until 1990.

**Subroutines save and restore whatever they happen to use**, symmetrically and by
hand:

```
11327:  push bx / push cx / push dx / push di / push si
        ...
1138F:  pop si / pop di / pop dx / pop cx / pop bx / ret
```

`bx`, `cx` and `dx` are caller-saved scratch under every DOS C convention, so a
compiler would not preserve them. The pattern occurs 21 times in each image.

Two supporting details. The stack segment is filled with 64 repeats of the
literal `STACK   `, which is the textbook MASM idiom
`DB 64 DUP('STACK   ')` in a `SEGMENT PARA STACK 'STACK'`. And there is no
runtime library of any kind in the strings - no `__acrtused`, no `DGROUP`, no
divide-by-zero or floating point handler text, no `Runtime error`, no vendor
banner. The only English in the binary is the game's own.

Note the lineage, because "originally" has layers. The strings say
`Adapted from Amiga version` and `MS-DOS (TM) version by SYNERGISTIC SOFTWARE,
INC.` / `Programmed by John Conley`, so this MASM code is a port. The Amiga
original would have been 68000 assembly, and the 1984 Boulder Dash it descends
from was 6502 on the Atari 800. Assembly the whole way down.


## The music

**Confirmed, and an earlier reading of it here was wrong in three ways at once.** The tune is a **tracker**, not a melody, it is **monophonic**, and its pitches are **four times lower** than the note table suggests.

**Note table** at `0x0E4C8` (VGA) and `0x0CA78` (EGA): 36 little-endian words of 8253 PIT divisors. The player **multiplies every entry by four** for the PC speaker before a note is played, at `0x14932`:

```
1493c: mov cx,0x24          ; 36 entries
1493f: lea bx,ds:0xe4c8
14943: mov ax,[bx]
14945: shl ax,1
14947: shl ax,1             ; x4
14949: mov [bx],ax
```

Miss that and the tune comes out between 1695 Hz and 12693 Hz, which is a whistle. Scaled, it sits between 424 Hz and 1259 Hz, a normal melodic register. A Tandy halves the table instead, so it plays the same tune a fourth lower.

**Tempo** is the word just before the note table, a constant 9 that nothing ever writes. A sub-step lasts `tempo + 1` ticks of the 60 Hz timer, so **166.66 ms**, six a second.

**Pattern bank** at `0x0E558`: 22 patterns of 8 events, two bytes each - a note index and a gate length in sub-steps. A note byte of `0xFF` does **not** mean rest. It means "nothing new here", and the gate counter from the last real note keeps running; the speaker is gated off when it reaches zero, and a new note pre-empts it. That pre-emption is the entire articulation model and it is what gives the tune its rhythm.

**Three order lists** at `0x0E6B8`, `0x0E71A` and `0x0E77C`, each 48 pattern numbers ending in `0xFFFF`. This is where the earlier reading went wrong: they are not three simultaneous voices. They are arrangements for two different sound chips, chosen at `0x14973` from a byte the startup probe sets after scanning the BIOS for the string `Tandy`. On an ordinary PC the game plays the **third** list, on the speaker, one note at a time. The first two drive a Tandy's two channels, and on that path the third is loaded and immediately discarded.

The decisive instruction is `0x149B5`:

```
149af: mov si,ds:0xe4c2           ; song position
149b3: shl si,1
149b5: mov si,[si-0x1884]         ; ds:0xE77C -> a PATTERN NUMBER
149ca: shl si,1 / shl si,1 / shl si,1
149d0: add si,cs:0x5eb2           ; + sub-step 0..7
149d7: mov bl,[si-0x1aa8]         ; ds:0xE558, the pattern bank
```

The value from the list is shifted left three times and used to index the bank, so it is a pattern number. Every entry in all three lists is 0 to 21, and exactly 22 patterns fit between `0x0E558` and `0x0E6B8`.

**The tune** is 64 seconds in G sharp minor, structured A B C, A B C, coda - 12 of the 22 patterns used, the other 10 dead data. Read the bank straight through instead and you get the 176 events of gibberish this file used to describe.

**Where it plays.** The player runs off the timer interrupt, once per tick, gated by `ds:0x1680`. That flag is set in the attract loop at `0xED62` and cleared the moment a game starts, so this is the title theme and **there is no in-game music at all**. The cave-complete jingle at `0xEE0C` is the same tune started at order position 44, which is the last four bars of its own coda.

**A single tune, proven.** A sweep of the whole image for any run of 12 or more words falling a semitone at a time finds exactly one note table. Only five instructions reference it, all inside this player. The 792 bytes from `0x0E4C8` are byte-identical in the EGA image at `0x0CA78`, tempo included, and both releases generate identical MIDI.

**Sound effects are a separate driver** at `0x133A5`, which settles another open item below: it is PC speaker only, writes hardcoded divisors straight to port `42h`, and never touches the note table.

## The debug keyboard

**Confirmed.** Both builds carry a developer keyboard reached from the pause
screen, and the two do not carry the same one. Nothing here is a cheat the game
advertises: there is no prompt, no message and no menu entry.

### Getting in

The pause loop tests a chord and a modifier before it will read a debug key -
`0x13253` in `unpacked/vga.bin`, `0x217F9` in `unpacked/ega.bin`:

```asm
13253: mov al,cs:0x1c
13257: add al,cs:0x1d
1325c: cmp al,0x2              ; both held
1325e: jne 0x13270
13260: mov ah,0x2 / int 0x16   ; BIOS shift flags
13264: test al,0x3             ; either Shift (EGA tests 0x2, left only)
13268: call 0x1328e            ; the debug keyboard
```

`cs:0x1C` and `cs:0x1D` are key-down flags kept by the keyboard handler, set on
press and cleared on release at `0x11874` and `0x11898`. `cs:0x1C` is scan
`0x48` and `cs:0x1D` is scan `0x50` - **Up and Down**, which on the numeric
keypad the game asks you to steer with are 8 and 2. So the way in is **Up and
Down held together with Shift, while paused**, and it cannot happen by accident
because the two keys are opposite directions.

The handler then reads keys in a loop, `0x1334B` jumping back to the top, until
a key sets `ah = 1` and leaves the pause behind it.

### What the keys do

| Scan | Key | VGA | What it does |
|---|---|---|---|
| `0x2E` | C | `0x1329A` | Collect one treasure. Calls `0x116A3`, which plays the collect sound, decrements the remaining quota at `ds:0x164A` and opens the exit at `ds:0x167A` when it reaches zero |
| `0x26` | L | `0x132AB` | `inc ds:0x2258` - one more life, then redraw |
| `0x32` | M | `0x132BD` | Ends the cave with `ds:0x16B8` forced to 3, so the next one is the world's last |
| `0x31` | N | `0x132DD` | Ends the cave as completed - sets both `ds:0x166E` and `ds:0x1686` |
| `0x19` | P | `0x132F7` | `add ds:0x225C, 1000` - a thousand points |
| `0x1F` | S | `0x1330B` | `inc ds:0x225E` - one more second |
| `0x14` | T | `0x1331A` | `ds:0x225E = 2` and leave, so the clock runs out immediately |
| `0x16` | U | `0x13330` | `ds:0x16A4 = 0`, the cheat off |
| `0x2C` | Z | `0x13340` | `ds:0x16A4 = 0xFF`, the cheat on |

Any other key returns without setting `ah`, which drops back to the ordinary
pause.

**The EGA build has three of these, not nine.** Its handler at `0x21834` tests
`0x26` (L, `inc ds:0x415A`), `0x31` (N) and `0x19` (P, `add ds:0x415E, 1000`),
and stops. There is no C, M, S, T, U or Z, and **no cheat flag at all** - the
EGA image has no counterpart to `ds:0x16A4`. So `Simulation.Cheat` is a VGA
feature, and the debug keyboard grew between the two releases.

### Where the cheat is read, and how the port folds it

Six sites read `ds:0x16A4`, every one the same shape - `cmp byte ds:0x16A4,0xFF`,
then `or byte ds:0x2262,4` to request the sound and jump past the normal
handling. None is on a death path, so the cheat is walk-through-anything rather
than invulnerability.

They are six because the original writes each direction out in full, and they
come in two groups:

| Sites | What they are | The port |
|---|---|---|
| `0x10162`, `0x102D6`, `0x103F6`, `0x10530` | one per direction, in the walk chain | `Simulation.Player.cs`, the `earlyCheat` test and the one after it |
| `0x1022B`, `0x103A8` | the two horizontal copies of the sideways push, each sitting between the boulder test and the random roll at `0x124B1` | `PushByWalking`, the test before the roll |

The port folds four direction copies into one method, so its two walk tests plus
one push test cover all six. **Nothing is left over**, which is what issue #94
asked to establish - there is no read site the port does not reach.

The position within the chain is not the same for every direction: going right or
up the cheat is tested before the blocking cases, going left or down after them.
That is only observable with the cheat on, and the port keeps it.

### R and S are ordinary keys, not debug keys

Neither build tests scan code `0x13` anywhere, so **R is not a debug key**. It
is a normal in-game key, matched on its ASCII rather than its scan code -
`0x11D7E` in VGA, `0x201A2` in EGA - and what it does is set the end-of-cave
byte (`ds:0x1686`, EGA `ds:0x35BE`), which the main loop tests at `0xEDF4`.
**R gives up on the cave.** `s` next to it toggles the sound.

That settles a claim from the Rockford Remake page, which has R making coins
appear. It does not; it restarts. See [`PRIOR-ART.md`](PRIOR-ART.md).

### The remake page's "dying on/off toggle" is a patch, not a feature

The same page gives offset `0x0B98` in the **packed** EGA executable as a toggle
for dying. Mapped through the unpacker it is not a toggle and not a flag.

`asahala/Rockford-Patcher` writes it as a single hex nibble -
`hex_data[:5937] + '1'` - and patching a copy, unpacking both and diffing them
changes **exactly one byte**: unpacked EGA `0x4300`, `0x02` to `0x01`.

Nothing in the image reads `ds:0x4300`. It is not a variable - it is one entry
in a table of nine word pairs ending in `FFFF`, running `0000 0002 0004 / 0100
0102 0104 / 0200 0202 0204` from `0x42F8`. Those are row and column offsets for
a **3 by 3 neighbourhood** in a grid two bytes to the cell, which is the stride
the engine's own direction deltas use. The patched word is the middle one, and
the patch takes it off the two-byte alignment every other entry keeps.

So it is a hex edit of shipped data that makes an explosion write somewhere it
was never meant to. No key turns it on, no flag guards it, and the unpatched
byte is `0x02` in every copy. **A patch that stops death is not a cheat the game
shipped**, and this file records it as the prior art's, not the game's.

## Was open, now settled

1. ~~**Per-cave treasure quotas and time limits.**~~ **Found** - four 40-entry word tables in the VGA image: required treasure at `0x245E`, treasure score before the quota is met at `0x24AE`, score after it is met at `0x24FE`, and the time limit at `0x26A2`. Indexed at `0xFE9D` by `raw world * 4 + stage`, and the menu's five entries map to raw worlds 0, 2, 4, 6 and 8 through the table at `ds:0x26F2` - so the playable records are 0-3, 8-11, 16-19, 24-27 and 32-35, not the first twenty. `Rockford.Formats.CaveMetadata` reads all four tables and the browser front end uses them. Values and proof: `astra-engine-audit.md` (the independent audit of the first engine, kept private).
2. ~~**Five cell values**: `0x38`, `0x70`, `0x78`, `0x7C`, `0xC4`.~~ **Decoded** - the loader at `0x108C4` names all five, and the reason they co-occurred with `0x74` is that `0x74` is the exit rather than Rockford. `0x38` and `0xC4` become the two activatable producers, `0x70` and `0x84` the two timed entities, `0x78` a multi-treasure worth `value - 0x6C` gems, `0x7C` a twenty-second time bonus. What their handlers then *do* is only partly ported - see `PORT-PLAN.md` item 14.
3. ~~The VGA `.SCM`/`.CCM` compression.~~ **Solved** - see above.
4. ~~**Sound effects.**~~ **Found** - the driver is at `0x133A5` to `0x13535`. Eight one-bit requests in `ds:0x2262`, highest priority winning and one sound audible at a time. The driver runs on every 60 Hz timer tick. Most requests have a fixed PIT divisor, but not all: the treasure and boulder sounds pick one of two divisors with a random draw, the footstep alternates between two, and the low-time alarm under ten seconds uses `(seconds << 4) + 0x200` (`0x13167`), so it rises in pitch as the clock runs down. The two holding tones, the tick and the alarm, speak only on every eighth tick. Nothing in it is digitized; it is a square wave like everything else. (Corrected 13 October 2026: this said each request has a fixed divisor. The code is `Simulation.Sound.cs` and `Simulation.Clock.cs`.)

5. ~~**The EGA palettes.**~~ **Solved** - 12 tables of 17 bytes at `0x04082`, in the
   world interleave, read as IRGB. See the EGA section above.

Nothing is open in this list any more.

## How the engine relates to this writeup

An independent disassembly audit on 15 September 2026 checked the eleven rules an
earlier version of this file asserted, against the code in `unpacked/vga.bin`. **All
eleven diverged.** The worst was that the player and the exit were the wrong way
round: authored `0x20` is the player, because the loader at `0x1093A` gives it a
two-cell sprite and stores its position in two globals, and authored `0x74` becomes
runtime `0x07`, the quota-gated exit at `0x14316`. Rockford started every cave
standing on the exit.

That engine has been retired. The rules above are now the ported ones, each citing
the instruction it came from, and the full report is
`astra-engine-audit.md` (the independent audit of the first engine, kept private). What remains unported is listed in
[`PORT-PLAN.md`](PORT-PLAN.md).

The honest limit on all of it: the port matches the disassembly **as read**. Nothing
here has been checked against a recorded run of the original game, so a mistake in
the reading would not have been caught.
