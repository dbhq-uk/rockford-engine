# rockford-engine

A C# reimplementation of the engine of *Rockford: The Arcade Game*, the 1988 MS-DOS
game, written from its disassembly one handler at a time, with the tools and the
format documentation that came out of the work.

The write-up, and both playable builds, are at **[rockford.dbhq.uk](https://rockford.dbhq.uk/)**.

## What is here

| Path | What it is |
|---|---|
| `src/Rockford.Formats` | Readers for the cave file, the EGA and VGA picture formats and the tables in the executable |
| `src/Rockford.Engine` | The simulation: one file per behaviour, the scan, the clock, sound requests and the viewport |
| `src/Rockford.Reference` | An 8086 interpreter that runs the original executable, so the port can be compared with it |
| `src/Rockford.Cli` | A terminal front end, a session inspector, and the sweep that runs the port beside the original |
| `web/Rockford.Web` | The browser front end (Blazor WebAssembly) |
| `tests/` | The rule tests, each tied to the handler it comes from |
| `tools/` | Python tools for the formats, the art pipeline and the checks |
| `docs/` | `FORMATS.md` (what is on the disks), `PORT-PLAN.md` (the engine, handler by handler), `MEMORY-MAP.md`, `PRIOR-ART.md` (earlier work and other versions), `CICODIS-FEASIBILITY.md` |

## What is not here

None of the original game: no disk images, no executable, no art, music, caves or
tables copied from it. This project's own art and audio for the enhanced build are
not here either. Both stay private, so anything that has to come down on request
can.

You need your own copy of the game to run the builds or the data tests. The test
suite runs without it: tests that need the game skip.

## Running it

```bash
dotnet test Rockford.slnx                              # without the game: data tests skip
ROCKFORD_DATA=/path/to/your/game ROCKFORD_EXE=/path/to/unpacked.bin dotnet test Rockford.slnx
node tools/jslifecycle.mjs && node tools/jscartoon.mjs
```

`ROCKFORD_DATA` is a directory holding the game's files; `ROCKFORD_EXE` is the
unpacked executable. `docs/FORMATS.md` says how the executable is packed.

## How far to trust it

The engine matches the disassembly as read. Its cave loader and its main-loop step
have been run beside the original's own code in an interpreter, in all forty caves
with nobody at the controls, and they agree for most caves; the residual is traced
to state that harness cannot set up. Real play, with input, has not been compared.
`docs/PORT-PLAN.md` has the detail.

## Licence

The code and the documentation are MIT licensed (`LICENSE`).

*Rockford: The Arcade Game* is copyright First Star Software, Inc. **Rockford** and
**Boulder Dash** are trade marks of BBG Entertainment GmbH. This project is not
affiliated with, endorsed by or licensed by either company. For a rights concern,
see [rockford.dbhq.uk/legal](https://rockford.dbhq.uk/legal#takedown).

This repository is exported from a private one, which holds the game data the tests
can be run against. Issues and corrections are welcome here.
