# Porting the engine to match the original

The audit in `astra-engine-audit.md` (the independent audit of the first engine, kept private) found that all eleven
rules this project asserts diverge from the executable. Dan chose a full port on
15 September 2026. This file is the register of that work.

## What "match" means here

The target is behavioural equivalence with the original's simulation, not a
bit-exact emulation of an 8086. Specifically:

- the same authored-to-runtime tile mapping
- the same scan order and the same intermediate states
- the same random generator, drawn the same number of times in the same places
- the same per-cave quotas, time limits and scores, read from the executable

Where the original's behaviour depends on interrupt timing rather than simulation
steps, the port models the interrupt counter rather than approximating it.

## The model the original uses

Established from the dispatch loop at `0x140DE` and the loader at `0x108C0`.

- A runtime cell is two bytes: a value byte and a facing/sub-state byte.
- Horizontal stride 2, vertical stride `0x100`, so a row holds 128 cells of which
  the leftmost 40 are the cave. A parallel plane sits at `+0x80` within the row.
- The scan runs **top to bottom, left to right**, 22 rows of 40.
- Values below 4 are inert and skip the dispatch. Everything else calls the handler
  from the table at file `0x13C50`, indexed by `2 * value`, with the handler address
  being `0xE9F0 + word`.
- One simulation step waits for at least 6 interrupt ticks (`0x1334F`). The interrupt
  runs at 60.0 Hz, from PIT divisor `0x4DAE`. So the simulation runs at 10 Hz and one
  interrupt tick is 1/60 s.

## Handler map, decoded from the dispatch table

| Runtime | Handler | What it is |
|---|---|---|
| `00`-`03` | inert | space, dirt, steel, ordinary wall |
| `07` | `0x14316` | closed exit, opens on the quota |
| `08`-`0B` | `0x14445` | activatable downward-producing entity |
| `0C`-`0F` | `0x140AE` | activatable upward-producing entity |
| `11`-`14` | `0x1467C`, `0x14126` | treasure explosion phases |
| `15` | `0x14632` | expanding wall |
| `16` | `0x14677` | expanding wall, deferred |
| `1B`-`1E` | `0x1467C`, `0x14130` | space explosion phases |
| `23` | `0x14322` | acid terminal: fall, or kill the clockwise family |
| `24`, `25` | `0x146A0`, `0x146B8` | acid, blocked once and twice |
| `2B`, `2F` | `0x1434E`, `0x14473` | acid settled; fire terminal |
| `F0`-`F4` | `0x1443D`, `0x145AF`, `0x146D0`, `0x13ED4`, `0x1460F` | deferred seeds, see below |
| `30`-`33` | `0x14301` | open exit |
| `40` | `0x1414A` | resting treasure |
| `42` | `0x14223` | falling treasure |
| `43` | `0x142F7` | falling treasure, deferred |
| `44` | `0x13EF7` | resting boulder |
| `46` | `0x13FD0` | falling boulder |
| `47` | `0x140A4` | falling boulder, deferred |
| `48`-`4B` | `0x13E50` | amoeba |
| `74`-`77` | `0x1467F` | timed converting wall |
| `78`-`7B` | `0x13ED8` | timed entity, tracked at `DS:1614` |
| `7C`-`7F` | `0x14613` | second timed entity, tracked at `DS:161C` |
| `8C` | `0x11B54` | enemy family, clockwise |
| `90` | `0x11B91` | enemy family, anticlockwise |
| `A0`-`A7` | `0x13E78` | what the first timed entity hatches into |
| `B0`-`B7` | `0x145B3` | segmented moving entity |

### The deferred seeds, `F0` to `F4`

Read the whole dispatch table rather than sampling it and five more one-shot values
appear. Each replaces itself with a real value and does nothing else:

| Seed | Becomes | Handler |
|---|---|---|
| `F0` | `28`, acid settled | `0x1443D` |
| `F1` | `2C`, fire head | `0x145AF` |
| `F2` | `25`, acid blocked twice | `0x146D0` |
| `F3` | `A7` | `0x13ED4` |
| `F4` | `B7` | `0x1460F` |

They exist for the same reason as the deferred boulder and treasure states, and the
rule is the same one the scan order imposes everywhere: **a handler writing into a cell
the scan has not reached yet must write something inert.** Fire moving left writes the
live `2C`, because that cell is behind the scan. Fire moving right writes `F1`, because
writing `2C` there would let the phase chain advance it to `2D` in the same step and the
projectile would lose a frame of its cycle.

The same effect is visible from the outside and is worth knowing when reading a test: a
projectile written *downwards* or *rightwards* is one phase further on by the end of the
step that created it, because the scan catches up with it.

## State chains, decoded from the dispatch table and the terminal handlers

`0x1467C` is a bare `inc WORD PTR [si]`, so a value in one of these runs simply
advances one step per scan visit until it reaches a terminal handler that replaces
it. The chains are longer and more numerous than the audit named.

| Chain | Terminal | Becomes |
|---|---|---|
| `11` `12` `13` | `14` at `0x14126` | treasure |
| `17` `18` `19` | `1A` at `0x1412B` | treasure |
| `1B` `1C` `1D` | `1E` at `0x14130` | space |
| `1F` `20` `21` `22` | `23` at `0x14322` | a downward projectile, see below |
| `28` `29` `2A` | `2B` at `0x1434E` | moves itself down one cell as `1F` |
| `2C` `2D` `2E` | `2F` at `0x14473` | moves itself up one cell as `2C` |
| `34` `35` `36` | `37` at `0x14135` | boulder |
| `38` `39` `3A` | `3B` at `0x1413A` | calls `0x1356C` |
| `3C` `3D` `3E` | `3F` at `0x14140` | boulder |
| `6C` `6D` `6E` | `6F` at `0x14441` | back to `6C`, an endless four-frame loop |
| `70` `71` `72` | `73` at `0x14145` | back to `70`, an endless four-frame loop |

So the game has **projectiles**, which the old engine has no notion of. `2B` walks
itself down a cell at a time and `2F` walks itself up, each re-entering its own
chain after the move. `0x14322` shows the downward one killing the clockwise enemy
family with a kind-1 blast centred one cell left of itself.

The two activatable producers feed them: runtime `08`-`0B` at `0x14445` is gated on
`ds:0x1678` and runtime `0C`-`0F` at `0x140AE` on `ds:0x166C`, both of which the
fire action sets.

Two more four-state entities appear when their timer passes `0x4B0`, which is 1200
interrupt ticks or 20 seconds:

- runtime `78`-`7B` at `0x13ED8` watches `ds:0x16AA` and becomes `A0`
- runtime `7C`-`7F` at `0x14613` watches `ds:0x16AC` and becomes `B0`

Walking into or firing at an egg sets only that worm's counter to `0x4B0`, so
that is what starts it: an egg of `78`-`7B` sets `ds:0x16AA`, an egg of `7C`-`7F`
sets `ds:0x16AC` (`Simulation.Player.cs`, the walk chain and the fire chain).
Corrected 13 October 2026: this said "Fire sets both counters to `0x4B0`, so
firing is what starts them."

## Scan loop, decoded at `0x140DE`

```
ds:0x1604 = 0          amoeba colony counter, recounted every scan
ds:0x16A0 = 0xFF       "the amoeba is enclosed", assumed true until a cell disproves it
for row in 0..21:
    for col in 0..39:
        al = value byte
        if al >= 4: call dispatch[al]
at the end:
    if ds:0x166A == 0xFF and ds:0x16A0 == 0xFF: call 0xEEED
```

`0x14122` displays as `call 0x1eeed`, which is the 64 KiB wrap: the real target is
`0xEEED`. Worth remembering when reading any near call in this image.

## Order of work

1. - [x] Random generator, ported from `0x124B1`.
2. - [x] Cave metadata: quota, time limit and both score tables from the executable.
3. - [x] Runtime cell grid and the authored-to-runtime loader.
4. - [x] Boulders and treasure, all eight states, with both blasts.
5. - [x] Amoeba, on authored `10`, with the per-neighbour draw and the real outcomes.
6. - [x] Explosions: two kinds, per-kind immunity, explicit phases.
7. - [x] Enemies: two families, two attempts then a stationary turn.
8. - [x] Walls: expanding wall on authored `08`, timed converter on authored `04`,
       pass-through on authored `0C`.
9. - [x] Player: pre-movement adjacency death, walking push with its random test,
       horizontal-only fire.
10. - [x] Exit and scoring.
11. - [x] Tests rewritten against the original's behaviour, not the old assumptions.
12. - [x] A browser front end on the new engine, with the original sprites.
13. - [x] Retire the old engine: `CaveSimulation`, `Cave`, `Tile`, `CaveBuilder` and
        the 37 rule tests written against them, and move the terminal front end over.
14. - [x] The parts still unported. **Every value in the handler table at `0x13C50`
        now dispatches**, and the 45 routines it names are all ported or are the bare
        `ret` at `0x1469F`. The producers are the fire tap (authored `38`) and the
        acid tap (authored `C4`); the projectiles are the fire that rises and the acid
        that falls, and **they explode when they meet**. Treasure dropped onto fire
        becomes a treasure-yielding enemy.

        Two things in the original wording were wrong. The **multi-treasure
        collectibles were already done**: authored `78` and `7C` load as `6C` and
        `70`, both dispatched and both collectable, and they are not timed entities at
        all. And the **"segmented mover" is not a separate entity** - it is how the
        two worms are built, so it was one job and not two.

        The worms are the evil one (authored `70`), which walks treasure back to
        boulders and boulders on to `0x90`, and the good one (authored `84`), which
        walks `0x90` back to boulders and boulders on to treasure. `0x90` is the
        **anticlockwise** family, the one that explodes into nothing - the earlier
        note calling it clockwise was wrong, and so was the port's `0x3B` case until
        this landed. See below.
15. - [x] CI: build with warnings as errors and run the rules. **The third part of
        this - "fail if derived game assets are ever committed" - is no longer
        true and no longer wanted.** `guard.yml` checks that `DATA-NOTICE.md` is
        still present, and that is all it checks. The derived assets under
        `wwwroot/game/` are committed deliberately (Dan, 16 September 2026): a
        fresh checkout that builds without running a tool first is worth more
        than the rule was. Corrected 19 September 2026.
16. - [x] The scrolling viewport, with the latches and the framing.
17. - [x] The HUD: lives, the world's own treasure name, the count, points and the
        clock. All five, with the score and the lives in the original's own form -
        see "The status row" below.

## The taps and the projectiles, ported

Done. `Simulation.Projectiles.cs`, with `ProjectileTests`.

Authored `38` is the fire tap and authored `C4` the acid tap. Each is a four-frame
animation that emits one projectile every eight steps once armed, and **firing at a tap
is what arms it** - the chain at `0x1200A` that the player code already had. The fire
tap adds 4 to the counter before testing it and the acid tap does not, so a cave holding
both never emits them on the same step.

Fire rises and acid falls, one cell per four-state cycle. Both of the rules that
`PRIOR-ART.md` had only in prose are now confirmed in the code:

- **Fire and acid destroy each other.** `0x1448A` tests the cell above against `20`-`2B`,
  the acid's whole state range, and throws a kind-2 blast. Fire meets acid at any point
  in the acid's life, not just its head.
- **Treasure struck by fire becomes a creature.** `0x1355E` writes `8C`, the clockwise
  family - the one that yields treasure when killed. So fire does not destroy treasure,
  it puts it behind something you now have to kill.

Fire that cannot rise goes sideways, preferring the direction it is already travelling,
which it remembers in the cell's state byte as `F8` for left or `FA` for right. Fire
with neither - the first one out of a tap - picks a side from a draw. Blocked both ways
it draws again and burns out three times in four, surviving where it stands otherwise.

Settled acid is not inert. When it cannot fall it crawls sideways along whatever is
holding it up, with the same direction memory and the same burnout as fire, and it
**attacks a creature beside it** where fire converts treasure instead. Its fallback to
the opposite side tests only for space, so it will not attack that way.

Two corrections to earlier notes in this file, both found by review:

- **The acid blast is not an original bug.** An earlier version of this section claimed
  its centre landed one cell left of the creature. It does not. `Blast` takes the
  upper-left of the 3x3 and the centre is `+0x102` further on, so `SI-2` centres on
  `SI+0x100`, exactly the creature below. Every falling-object caller subtracts the same
  two bytes. Anyone "fixing" this would introduce the displacement it claimed to describe.
- **The fire/acid range is `20`-`2B` and excludes the acid head at `1F`.** That costs
  nothing, because the scan runs downwards: acid above fire has already advanced past
  `1F` by the time fire's terminal looks up at it. Fire can never observe `1F` above
  itself.

## The worms, ported

Done. `Simulation.Worms.cs`, with `WormTests`.

**A worm is not a cell the scan moves.** It is a routine the main loop calls once a
step, at `0xEDBC` and `0xEDBF`, ahead of the scan and holding six globals of its own.
The cells the scan sees are the trail it has already laid. That is why a scan-shaped
model had nowhere to put one, and why nothing in the handler table ever moved one.

`0x1194B` and `0x11A68` are the same routine twice over. Each holds a direction, a
length, a head cell, a tail cell and two flags, and both index the delta table at
`ds:0x1656` that the enemies use. They differ in four things only: their globals, the
seed they leave behind, the body value they write, and the transform.

**Where the head comes from.** Hatching at `0x13EE4` sets the length to one and
nothing else. The head pointer is seeded much earlier, by the loader at `0x109FB`,
which records the cell the authored egg was placed in - so a host that builds the
Simulation itself has to pass `CaveLoader.TimedEntityACell` in, or the worm hatches
with its head still at the zero `0x10878` left and grows out of the top-left corner.
Two hours went into finding that write, because a byte-pattern search for stores to
`ds:0x1614` missed the `di` form.

**The body.** `A0` is the newest cell and `A7` the oldest. The scan handler at
`0x13E78` ages every segment by one and erases anything that passes `A7` - except the
cell the tail pointer names, which is pinned at `A7` until the movement routine erases
it for being the ninth. The pointer advances in the handler, not the movement routine:
the cell passing through `A6` claims it on the way to `A7`. That pinning is the whole
reason a worm's drawn length equals its counted one.

**It reverses rather than turning.** Boxed in with a body behind it, `0x119F4` swaps
which end is the head, and the next scan mirrors every segment - so it retraces the
cells it laid instead of pushing into whatever stopped it. Boxed in with no body, it
freezes: `0x13EB1` makes the body handler return, which is what stops a one-cell worm
ageing itself out of existence.

**Three draws, and the count depends on what is in front.** Anything in front costs
one draw, to choose which way to turn (`0x1197B`). If both sides are blocked too,
the worm is stuck and takes one more, for its new direction (`0x11A0B`). The
transform runs before the turn draw, and only the first worm's boulder branch draws:
a one-in-eight roll for every boulder it meets. So for the first worm a boulder
takes two numbers out of the shared stream, anything else takes one, a stuck worm
one more on top, and clear space takes none. (Corrected 13 October 2026: the count
left out the stuck draw. `WormTests.cs` tests the one-and-two case.) The second worm
converts every boulder it meets with no draw at all; the two routines are otherwise
identical, so that asymmetry reads as a porting slip and is not.

**The transformations are PRIOR-ART's, exactly.** Neither worm writes the thing it is
making: both write the first value of a counting chain the scan was already walking,
so the change takes four more scans and is visible while it happens. Every one of
those chains was already ported. The first worm sends treasure to `3C` and boulders to
`38`; the second sends `0x90` to `34` and boulders to `17`. Read together they are one
pair - one spoils a cave in the direction the other mends it.

**A defect this turned up.** The port's `0x3B` case, added with the hatching, named
`EnemyClockwise`. `0x1356C` writes `0x90`, which is `EnemyAnticlockwise`; `0x8C` is the
clockwise one. The two families turn opposite ways and explode into different things,
so it was not cosmetic - and the second worm's transform converting `0x90` is what
made it obvious, because the pair only makes sense if both halves name the same
creature.

**One global that outlives the cave.** `ds:0x1610` and `ds:0x1618`, the two headings.
The loader clears the lengths at `0xFEBA` and the head and tail pointers at `0x10878`,
and leaves these two alone - so a worm hatches facing wherever the last worm was
facing, possibly several caves ago. `CarryGlobalsFrom` carries them.

Nothing masks a heading on the way out. `0x11969` shifts the whole word and indexes
the delta table with it, so a heading of 4 would read the zero word sitting behind the
table and the worm would try to move nowhere at all. The two turns mask with 3, and so
does the draw at `0x11A0B`, so a running game never produces one. The port therefore
does not mask on the read either - masking would be a difference from the original
that nothing could ever observe - and instead the two public setters refuse anything
outside 0 to 3, which keeps the unreachable state unreachable. Astra found the mask on
review, where the first draft had it.

The four flags at
`ds:0x1698` to `ds:0x169E` carry too and are not carried here, because they cannot be
read stale: the movement routine clears its pair at the top of every step, and the
only handlers that read them are body cells, which exist only once the length is
non-zero and the routine therefore runs.

## The status row

Done. `HudFormat.cs`, with `HudFormatTests`.

**The original's HUD is four bare numbers on one text row, and no labels.** Every one
goes through a single routine at `0x1220D` that sets the BIOS cursor from `ds:0x224E`
and `ds:0x2250` and teletypes one character with the attribute in `ds:0x1628`. The
column is incremented between characters. There are exactly four cursor placements on
row 1 in the whole executable:

| Column | Field | Chars | Colour | Routine |
|---|---|---|---|---|
| 1 | spare lives, and only at five or more | 3 | `ds:0x162C` | `0x1389F` |
| 15 | treasure still needed | 3 | `ds:0x162C` | `0x130E0` |
| 24 | score | 6 | `ds:0x162A` | `0x12255` |
| 35 | seconds left | 3 | `ds:0x1626` | `0x122E4` |

The other fourteen placements in the binary are all row 6 or 7, which is the menu and
the dialogue boxes.

**This port does not draw that row, by an earlier decision.** The canvas is 320 by
176, which is the play field; the original's screen is 320 by 200 and the missing 24
pixels are three character rows. The port puts the same numbers in the page around the
canvas. So the work here is formatting, not rendering.

**The score is six digits, zero padded, and that is what the split score is for.**
`0x12267` reads the carry count for two digits and `0x12293` reads the points for
four. The two words are not an arbitrary way to hold a number, they are the two halves
of this field - so 450 points reads `000450`.

It is also **the one field with no blanking**. The other three replace a leading zero
with a space; this one counts up from `'0'` and prints whatever it reaches. That
asymmetry is deliberate and a player sees it on the first gem of a new game.

**And it saturates.** `0x1226B` clamps the carry count to 99 *for the display*,
without writing it back - so the field sticks at `999999` while the real score carries
on and the bonus lives keep arriving, because `0x12244` reads the same counter.

**The lives number is one less than the lives.** `0x13895` is a `dec` before the
digits: it counts the spares, not the life being used, so a fresh game shows four. The
original only prints it at five or more; below that it blits up to three 16 by 16
sprites into the status strip at `0x1391B`, one per spare, which is why it needs a
number at all and why the number appears exactly where the sprites run out. Those
sprites are in the strip the canvas does not draw, so the port shows the count at
every value - a HUD that went blank below five would be worse than either original
behaviour.

**The clock and the quota keep their own format and the port does not copy it.** Both
are the same eight instructions twice, and both pad the hundreds digit with a space
rather than a zero - `0x122F6` loads `0x20`, not `0x30` - so nine seconds reads `" 09"`.
That space is a text-mode alignment artefact, because BIOS teletype output cannot
right-align a field. The HUD here is DOM with `font-variant-numeric: tabular-nums`,
which holds the column width by itself: the same result by a different mechanism.
Reproducing the space would be copying the mechanism rather than the behaviour.

One thing worth keeping from those routines. The hundreds digit is a **single
subtract rather than a loop**, so it can only ever be a space or a `1`; 200 or more
would carry into the tens loop at `0x1230B` and print punctuation where a digit
belongs - `:` at 200, `;` at 210. The original gets away with it because of the data,
not the code: the longest cave in the table at `0x26A2` is 180, `0xFEAF` adds one, and
the most time bonuses authored into one cave is two. **The highest reachable clock in
the forty is 191**, in cave 34, and `GameDataTests` asserts that against the shipped
files. Eight seconds of headroom, which is why the port does not clamp something the
original leaves unclamped.

## The host, and how this is tested

`Home.razor` was 1,026 lines with 38 JS interop calls in it, and nothing tested any of
it - issue #24. The fix was not a test framework. The loop was a `while` inside
`OnAfterRenderAsync` driving a `Stopwatch`, and no component-testing library can drive
that; the reason it was untestable was its shape.

**`GameHost` is the part that is not one cave**: which cave, which screen, the lives,
the score, the status row, and what a finished or failed cave leads to. It is the
layer the original keeps in its main loop at `0xEDA5` and its cave-advance routine at
`0xFE03`, and none of it needs a browser. What does is a `PlayEffects` record it hands
back - sounds to play, a sting to start, the sprite sheet to change, the tap set to
clear. The host decides and the shell carries it out.

`Home.razor` is about 1,500 lines now and holds the canvas, the audio, the input
settings and the asset fetches. It said 800 until 19 September 2026.

**`StatusBar` is its own component** for the same reason. Inside `Home` the row could
not be rendered without a JS module, four HTTP fetches and a game loop; on its own,
bUnit renders it in a line. And it takes **one parameter rather than eleven** - a
`StatusRow` the host builds - because eleven bindings were eleven lines that could
name the wrong property with nothing to catch it, which is exactly how the lives field
came to show the total rather than the spares.

### Which tests go where

Microsoft's guidance is to choose the scope per scenario rather than per project, and
its decision table is what this follows:

| What | How | Why |
|---|---|---|
| Engine rules | xUnit | No browser in sight |
| The host loop, cave advance, scoring, the status row's values | xUnit against `GameHost` | Same - the interop is behind `PlayEffects` |
| The status row's markup | **bUnit** | "No disadvantages to choosing unit testing" for a component with no JS interop |
| The canvas, the audio graph, teardown | `tools/jslifecycle.mjs` | The guidance's own advice for a component whose JS is large and whose interface to it is small: test the two parts separately |
| Wall-clock pacing | `tools/pace.mjs`, by hand | Headless Chrome, and a shared runner measures timing worst |

**bUnit v2 is the line with `net10.0`**; v1 stopped at .NET 8 and never got it, and v2
renamed `TestContext` to `BunitContext` among other breaking changes. It is
third-party and Microsoft says so plainly.

**Playwright was deliberately not here, and now it is.** The same guidance calls
E2E "less reliable" and "sensitive to the environment", and the argument here was
that the one thing it would add - proving the WASM runtime boots - is what
`tools/pace.mjs` already does when it is run.

> **Superseded, 19 September 2026.** "When it is run" was carrying the whole
> argument, and nothing ran it. `scripts/smoke-boot.mjs` drives chromium through
> both builds to a painted cave and `release.yml` runs it on every release. The
> reasoning is under "What CI checks about the published site" below; it is
> repeated here because this is where a reader looking for "should we add
> Playwright" arrives, and the answer they found was the old one.

### Two things this turned up

**The clock was being paid twice over.** `CashInTime` does both - it adds the seconds
to the cave's own score delta and returns them - so awarding the return value left the
same points in the delta for the next drain to find. Nothing claimed them, because the
hold ticks never drain and the cave is thrown away first, but it was a double award
waiting for someone to move a line. The host drains instead: same total, one route.

**A cave used to show the last one's clock.** The component never set the seconds on
load, so the status row kept the previous cave's for 100 ms - and on a fresh game that
was one frame of a red `time 0`, because zero is under the warning threshold. That is
the one deliberate behaviour change in the extraction.

## The observation surface, and why it is allowed to exist

The engine is frozen. It is the only part of this project that is interesting, it was
proved line by line against the disassembly, and a change in it invalidates that. So
the presentation gets one narrow window into it and nothing more.

**What it reports.** Four things, all after the fact: a cell's contents moved from one
address to another, a falling object came to rest, an explosion happened at a centre
cell, and the player fired at a cell. `Observation.cs`, drained through
`Simulation.TakeObservations()` and handed on by `PlayEffects.Observations`.

**Why it has to exist at all.** Consecutive snapshots of the grid cannot always tell
the truth. Two identical boulders side by side, one of which moved, produce the same
pair of before-and-after pictures as the other one moving, and movement and
replacement are indistinguishable. A renderer must snap the sprite where the
association is ambiguous rather than invent a trajectory, so what cannot be inferred
has to be said out loud.

Firing is the second case. `SoundEvent.Blast` covers the fire button *and* an
explosion - one request serves both in the original, deliberately - and a sound frame
carries no location at all. A front end shaking the screen on every `Blast` would
shake it on every shot.

**The boundary.** `Observe` appends to a list and does nothing else: no branch on
simulation state, no write to the grid, no draw from `Random`, no callback. Every call
site sits *after* the writes it describes. Deleting every one of them would leave the
simulation running exactly as it does now.

**And that is a test rather than a claim.** `FingerprintTests` hashes the whole state
of a scripted run step by step and compares it against numbers captured from the engine
at `45808ad`, **before any of this was added**. If a future change makes one of them
fail, the engine's behaviour has moved and that needs saying out loud rather than being
absorbed by updating the number.

**The first version of that test was much weaker than it was described as being**, and
the correction is worth keeping because the failure is an easy one to repeat. It built a
`Simulation` and never set `PlayerCell`. The default is `-1` and `PlayerTick` returns
immediately on that, so putting a player tile in the grid did nothing: all three traces
ran with an inert player, and the instrumentation added to `Walk` was covered by none of
them. Found by Astra on 19 September 2026 - `docs/astra-review-2026-09-19.md`.

The test now sets the pointer, asserts in each scenario that the thing it is named for
actually happened, covers the score delta, the producer flags, the amoeba cap and the
worm headings that were missing, and adds a fourth trace through `GameHost` for the
lives, the holds, the cave transitions and the globals that outlive a cave. All four
baselines were re-captured at `45808ad`.

**What is instrumented.** Boulders and treasure, in every direction they fall and roll.
The player's walk, and both push paths - by walking into a boulder and by firing at one.
Every enemy move, which all pass through one method. The two projectiles, which advance
one cell every four steps. `Blast` and `Fire`.

**What is not, and will not be.** The worms and the amoeba. A worm grows a new head and
erases its tail: no segment travels anywhere, so a `Moved` report would be inventing
one. Amoeba growth is creation rather than movement, for the same reason. Both snap, and
that is the correct picture rather than a gap.

Also not instrumented: the transformations at the timed and pass-through walls, which a
snapshot diff can see for itself.

## What CI checks about the published site

Added 19 September 2026, and it closes a gap that had been open long enough to
let a deploy go out without it.

`release.yml` runs two things `build.yml` never did. The `site` job runs
`site/tests/` - a content suite and a hundred-odd gate cases, every one of which
would have caught the authentication bypass of 18 September. They had never been
executed by a workflow. The `release` job assembles both presentations and serves
them through `wrangler pages dev`, which is workerd, the runtime Pages actually
uses, and then asks it for things: the gated paths and their assets must refuse
without the password, both builds must be reachable with it, and the VIP build
must 404 on every file the stripping removed.

**And since 19 September it drives a real browser.** `scripts/smoke-boot.mjs`
loads each build in chromium, presses through the title and the world chooser,
and waits for a painted cave - failing on any request the page made and did not
get. Status codes are not a running game: delete both builds' `_framework`
directories and every URL the checks above ask for still answers exactly as they
expect. Worse, the 404 assertions on the stripped files are satisfied *by* a
build that genuinely needs one of them. Only running the thing can tell the
difference, because only the running thing asks for what it actually needs.

**Those are two different claims and only one of them was being checked.**
`gate.test.mjs` proves the gate is correct. It cannot prove the gate is reached,
and that is precisely what was false when the first assembly put `functions/`
inside the asset directory: Pages never loaded it, the faithful build was served
ungated, and every unit test passed.

This corrects an argument made further up this file - that timing sensitivity
makes browser integration checks generally unnecessary. That conflates two
things. "The clock runs at exactly this rate" is genuinely hard to assert on a
shared runner and stays manual, in `tools/pace.mjs`. "The published application
boots, and refuses what it should" is not hard at all, and was simply not being
asked.

## Two host actions the original does not have

`GameHost.RestartCave` and `GameHost.AbandonToChooser` are **a deliberate exception
to the engine freeze, and the only one besides the observation surface.** They are
new decisions rather than ported behaviour. The original's only restart is `R`,
which gives up the cave at the cost of a life (`FORMATS.md`, "R and S are ordinary
keys"), and it has no way to leave a cave for the chooser part-way through.
(Corrected 13 October 2026: this said the original has no restart.)

They are stated here because "the engine is frozen" and "these two methods mutate
engine state" cannot both be left unqualified. The freeze is about not changing what
the port *reproduces*. These add something it never had, in the host - the layer that
was always the game outside one cave - and they touch no handler, no scan order and
no random draw.

What holds them: `FingerprintTests` proves nothing ported moved, and eleven tests in
`GameHostTests` cover what they do. Note what the fingerprint does **not** do - its
session trace never calls either method, so it is evidence that they did not disturb
anything, not evidence that they are correct. That is what the tests are for.

## Things the audit could not settle

Carried here so they are not lost:

- ~~Complete behaviour of the two timed entities and the rare collectibles.~~
  **Settled from the code.** `0x1194B`, `0x11A68`, `0x13E78` and `0x145B3` are read
  and ported; see "The worms" below. PRIOR-ART's description of the transformations
  turned out to be exactly right, down to the one-in-eight.
- Effective random frequencies during play, since input and sound also draw.
- Behaviour under interrupt delay, pause and 16-bit timer wraparound.
- EGA and VGA behavioural equivalence.
- A recorded differential replay against the original. Until that exists, "matches"
  means "matches the disassembly as read", not "verified against a running game".

  **The candidate reference was statically tested on 20 September 2026** -
  [`CICODIS-FEASIBILITY.md`](CICODIS-FEASIBILITY.md). It was not built and not
  run. The executable cicodis translates is byte-identical to
  `unpacked/ega.bin` bar twelve bytes of inert stack padding, so it is the same
  program. But it is the EGA release and every address in this file is VGA, and
  the two are separately compiled - so they cannot be lined up by address at
  all, which is the route [`PRIOR-ART.md`](PRIOR-ART.md) implied and which is
  now struck.

  **And the translation is wrong in at least one place.** Its patched treasure
  routine zeroes the quota on the first gem, where the original's `JNS` at
  `0x1FAF3` clamps only on a negative. So the reference is not an oracle until
  `rockford.patch` has been audited.

  What is left is a comparison at the cave grid rather than the instruction,
  which the session recorder makes practical: `SessionRecording` gives a
  canonical input sequence, and a cicodis build instrumented to dump its 40 by
  22 grid once a step can be compared against this engine cell by cell. That
  never needs the two builds to agree about where anything lives.

  **The first thing such a comparison would trip over is already known.**
  `GameHost.Load` seeds every cave with `0x04D2` and does not carry the seed
  across, while the original carries one continuing stream. That is a deliberate
  difference, it is not a defect, and a trace comparison that did not account
  for it would report one.

  **The original's own code has since been run against the port** (#82, closed
  22 September 2026, delivered in #83, #91 and #92; recorded here 13 October
  2026, which this item did not say). The interpreter in `src/Rockford.Reference`
  runs `ROCKFORD.EXE` itself, and `Rockford.Cli`'s `Sweep` compares it with the
  port. Against the four targets at the top of this file:

  - **The tile mapping is established by execution.** The loader, all 40 caves
    and all 256 authored byte values: 0 cells differing.
  - **The scan order and intermediate states agree for most caves.** The main
    loop's step body (`0xEDBF` to `0xEDD9`) against `Simulation.Step`, 50 passes
    a cave, all 40 caves, every one of the 2,816 cells compared after each pass,
    with nobody at the controls.
  - **The generator's arithmetic is established, the draw count has a known
    gap.** Its state is compared after every pass.
  - **The quotas, time limits and scores already held**, because `CaveMetadata`
    reads them from the executable.

  The residual is two things. One extra port draw in a few caves, for a
  landing's pitch, past a gate at `ds:0x1684` that the harness cannot set up.
  And `FFFE` against `FFFF` in a few cells of the `+0x80` plane. Both are
  harness limits and neither is shown to be a port fault. Real play, with input,
  has not been compared yet, so "verified against a running game" holds only for
  a cave nobody is playing.


## The scrolling viewport

The browser front end is SENT the whole cave, 40 by 22, and draws a window of it.
The original never had the whole thing to hand at all: its renderer at `0x10E6E`
walks a window of **20 columns by 11 rows** and scrolls it to follow the player:

> **Corrected, 19 September 2026.** This said the front end "draws the whole
> cave". It has not since the viewport landed - `draw(tiles, originColumn,
> originRow, ...)` takes the full grid and a camera origin, and paints 20 by 11
> from it. Sending the whole cave and drawing a window of it are different
> things, and the sentence conflated them in the section that exists to explain
> the difference.

```
10e73: mov dx,0x0b          ; 11 rows
10e8b: mov cx,0x26          ; 38, counted down by 2, so 20 columns
10e76: mov si,ds:0x2248     ; the viewport origin, a raw grid offset
```

Cave row `v` of the window is drawn at `y = 24 + 16*v` and column `c` at `x = 16*c`,
so the play field is 320 by 176 and the top 24 rows of the screen are the HUD.

**This is why the cave looks too small, and it is not a sprite problem.** Showing 20
by 11 instead of 40 by 22 halves the logical surface, which lets the integer scaler
pick the next step up, so a 16 pixel tile lands at 32 or 64 CSS pixels instead of 16
or 32. The art does not need touching.

### How the scroll actually behaves

The routine is at `0xFF87`, reached during play from `0x11F0E`. It is not a
stateless dead zone. It keeps four direction latches, so once it starts moving it
keeps going until the player is near the middle, which is hysteresis rather than a
per-frame recentre:

| Direction | Starts when | Stops when |
|---|---|---|
| left | player within 4 cells of the left edge | player reaches relative column 9 |
| right | player at relative column 15 or more | player reaches relative column 9 |
| up | player within 2 rows of the top | player reaches relative row 5 |
| down | player at relative row 8 or more | player reaches relative row 5 |

It moves one tile per eligible tick, both axes can move on the same tick, and the
origin clamps at column 20 and row 11.

Two traps in reading the comparisons. Horizontal values are **byte** offsets, so
`0x0A`, `0x12` and `0x1C` mean 5, 9 and 14 cells. Vertical ones look like half rows
because positions advance by `0x100`.

### Where it belongs

In C#, as a small pure component owned by the web front end - not in JavaScript,
where the rules could not be unit tested, and not inside `Simulation.Step`, which
would make the camera part of the cell scan. The simulation keeps updating the whole
cave; what is visible must never touch physics or scan order.

### Decode before writing it

**Written since. The first two are answered in `Viewport.cs` and are kept here
as the questions they were, not as work outstanding** - this section said
"confirm which cave that is" after the port had confirmed it, which reads as an
instruction. Corrected 19 September 2026.

1. ~~The initial framing.~~ **Answered.** The cave framed directly at `0x0B0A`
   by `0xFE82` is raw world 4, stage 2 - column 5 of row 11 - and it skips the
   settling loop entirely. `Viewport.Frame`.
2. ~~Where the four latch bytes are initialised, and whether they survive
   between caves.~~ **Answered.** `ds:0x168E`, `0x168C`, `0x168A` and `0x1690`,
   written by nothing outside `0xFF87`, and they **do** survive: a latch on when
   a cave ends is still on when the next is framed. `Viewport`.
3. Every caller of `0xFF87`. The fire path appears to return before reaching it,
   so firing may not scroll where walking does. **Still open.**
4. What `ds:0x224C` is for. It snapshots the old origin at `0x11BF1`. **Still
   open.**

### Sprite upscaling

Not needed, and not recommended as a default. Keep the sheets native, keep nearest
neighbour, and let the integer CSS scale do the enlarging: that is exact, cheap, and
keeps the animation frames consistent. A pixel-art filter like hq2x or xBRZ invents
contours and colours and would shimmer between frames, which is worse than chunky.

If an enhanced mode is ever wanted, do it at asset build time, scaling each 16 by 16
sprite **separately** into a new atlas with its own coordinates. Running a filter
over the whole sheet would sample neighbouring sprites, because the sheet's twelve
bands are not on a regular grid.
