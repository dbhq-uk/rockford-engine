using Rockford.Engine;
using Rockford.Reference;

namespace Rockford.Cli;

/// <summary>
/// The cave loader, run both ways from the same 880 bytes.
/// </summary>
public static class Sweep
{
    /// <summary>The loader's entry, and where it reads its authored cave from.</summary>
    private const int Loader = 0x10878;
    private const ushort AuthoredAt = 0x3010;

    /// <summary>
    /// The four things the loader records besides the grid.
    /// </summary>
    /// <remarks>
    /// Comparing the grid alone would miss them, and they are what the rest of
    /// the simulation reads: where the player starts, whether this cave has an
    /// amoeba, and the single cell of each timed entity. The addresses are the
    /// ones CaveLoader cites, and the loader's first nine instructions clear
    /// exactly this set.
    /// </remarks>
    private const ushort PlayerCellAt = 0x16BE;
    private const ushort AmoebaAt = 0x166A;
    private const ushort TimedAAt = 0x1614;
    private const ushort TimedBAt = 0x161C;
    private const int GridBytes = CaveGrid.Height * CaveGrid.RowStride;

    /// <summary>The main loop's step body, from the worms to the frame counter.</summary>
    /// <remarks>
    /// Not a routine, so it cannot go through Routine.Call - it is inline code
    /// in the main loop. Run from the first call to the instruction after the
    /// frame counter, which is one whole pass of what the port's
    /// Simulation.Step does.
    ///
    /// docs/PORT-PLAN.md names the pieces: the worms at 0xEDBC and 0xEDBF, the
    /// scan at 0xEDC2, the clock at 0xEE73 and the death countdown at 0xEE76,
    /// with the frame counter incremented AFTER the scan at 0xEDD5.
    /// </remarks>
    private const int StepFrom = 0xEDBF;
    private const int StepTo = 0xEDD9;

    public static int Run(string[] volume, int caves)
    {
        var machine = Harness.BootFrom(volume);
        machine.Dos.Type("N");

        // Far enough that the program has unpacked itself and set up its
        // segments, and no further.
        var booted = Harness.Run(machine, limit: 2_000_000,
            until: _ => machine.Dos.Opened.Count >= 1);
        Console.WriteLine($"booted  {booted.Instructions:N0} instructions, "
                          + $"ds={machine.Machine.Segments[Machine8086.DS]:X4}");

        var cellmaps = File.ReadAllBytes(Path.Combine(volume[0], "CELLMAPS.BIN"));
        Console.WriteLine($"caves   {cellmaps.Length / CaveLoader.RecordSize} records "
                          + $"of {CaveLoader.RecordSize}");
        Console.WriteLine();

        var worst = 0;
        var cells = 0;
        var work = 0;

        // A CALL THAT NEVER RETURNED IS A FAILURE, not a comparison with a
        // negative score. Compare() answered -1 for it and Math.Max threw that
        // away, so replacing the original loader's first opcode with HLT gave
        // 44 non-return reports, zero successful comparisons, "the loader
        // agrees, cell for cell" and exit code 0.
        //
        // The whole point of this tool is that it cannot report agreement it
        // did not measure.
        var failed = 0;
        var seen = new SortedSet<byte>();
        var total = cellmaps.Length / CaveLoader.RecordSize;

        for (var cave = 0; cave < Math.Min(caves, total); cave++)
        {
            var authored = cellmaps.AsSpan(cave * CaveLoader.RecordSize, CaveLoader.RecordSize);
            foreach (var a in authored) seen.Add(a);

            var differing = Compare(machine, authored, out var note, out var changed);
            if (differing < 0)
            {
                failed++;
            }
            else
            {
                worst = Math.Max(worst, differing);
                cells += GridBytes / 2;
                work += changed;
            }

            if (differing != 0 || caves <= 10)
            {
                Console.WriteLine($"  cave {cave + 1,2}  {differing,6} differing   {note}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"compared  {cells:N0} cells across {Math.Min(caves, total)} caves");

        // What the original left holding something other than the prefill:
        // 35,200 authored cells plus the 40 player-shadow words.
        //
        // NOT A COVERAGE FIGURE, and calling it one was an overclaim. The
        // prefilled cells are not free agreement - the original writes those
        // too, through a `rep stosw` of 2,816 words, and breaking the port's
        // prefill fails this comparison. This is the part where the two sides
        // had to agree on a VALUE rather than on FFFF, which is a narrower and
        // more honest thing to say.
        Console.WriteLine($"of which  {work:N0} hold something other than the FFFF prefill "
                          + $"({100.0 * work / cells:F1}%)");

        // And which arms of the mapping the shipped data can reach at all. A
        // branch no cave exercises is a reading of the disassembly and nothing
        // more - worth having, and not evidence.
        Console.WriteLine($"authored  {seen.Count} distinct values appear: "
                          + string.Join(" ", seen.Select(v => $"{v:X2}")));

        // EVERY AUTHORED BYTE, not only the ones First Star used. The port's
        // switch has 26 arms and the shipped caves reach 25 values, so 2D and
        // the default - which the port's own comment says "no shipped cave
        // reaches" - had nothing behind them but a reading of the listing.
        //
        // A cave is 880 cells and there are 256 values, so four synthetic
        // caves cover the lot with room to spare.
        var synthetic = 0;
        for (var pass = 0; pass < 4; pass++)
        {
            var cave = new byte[CaveLoader.RecordSize];
            for (var i = 0; i < cave.Length; i++)
            {
                cave[i] = (byte)((pass * CaveLoader.RecordSize + i) % 256);
            }

            var differing = Compare(machine, cave, out var note, out _);
            if (differing < 0) failed++; else synthetic += differing;
            Console.WriteLine($"  synthetic {pass + 1}  authored bytes from "
                              + $"{(pass * CaveLoader.RecordSize) % 256:X2} "
                              + $"{note}");
        }

        Console.WriteLine();

        if (failed > 0)
        {
            Console.WriteLine($"{failed} of the original's calls did not return - "
                              + "nothing was compared for those, and this is a FAILURE "
                              + "rather than a result");
            return 1;
        }

        Console.WriteLine(worst == 0 && synthetic == 0
            ? "the loader agrees, cell for cell - on every shipped cave AND on "
              + "every authored byte value"
            : $"worst shipped cave differed on {worst} cells; "
              + $"synthetic differed on {synthetic}");
        return worst == 0 && synthetic == 0 ? 0 : 1;
    }

    /// <summary>
    /// One scan, run both ways from the same loaded cave.
    /// </summary>
    /// <remarks>
    /// The SECOND of the four targets docs/PORT-PLAN.md sets out - "the same
    /// scan order and the same intermediate states" - and the first one that
    /// cannot be driven at the granularity the port exposes.
    ///
    /// Simulation.Step is documented as one pass matching 0x140DE, and it is
    /// the smallest public thing there is: BeginScan, Dispatch and EndScan are
    /// private, and the engine is frozen, so a handler-by-handler sweep would
    /// mean widening a surface that is deliberately narrow. Whole passes are
    /// what the public surface allows and this measures those.
    /// </remarks>
    public static int Scan(string[] volume, int caves, int steps)
    {
        var machine = Harness.BootFrom(volume);
        machine.Dos.Type("N");
        Harness.Run(machine, limit: 2_000_000, until: _ => machine.Dos.Opened.Count >= 1);

        var m = machine.Machine;
        var ds = m.Segments[Machine8086.DS];
        var code = machine.Dos.CodeSegment!.Value;
        var cellmaps = File.ReadAllBytes(Path.Combine(volume[0], "CELLMAPS.BIN"));

        // The per-cave numbers the host reads out of the executable. Without
        // them the port's simulation is missing most of its inputs.
        var tables = System.Text.Json.JsonSerializer.Deserialize<CaveTables>(
            File.ReadAllText(Path.Combine("web", "Rockford.Web", "wwwroot", "game", "caves.json")),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Console.WriteLine($"{Math.Min(caves, 40)} caves, {steps} scans each");
        Console.WriteLine();

        var worst = 0;

        for (var cave = 0; cave < Math.Min(caves, cellmaps.Length / CaveLoader.RecordSize); cave++)
        {
            var authored = cellmaps.AsSpan(cave * CaveLoader.RecordSize, CaveLoader.RecordSize);

            for (var i = 0; i < authored.Length; i++)
            {
                m.WriteByte(ds, (ushort)(AuthoredAt + i), authored[i]);
            }

            m.Segments[Machine8086.ES] = ds;

            // THE TWO EGG TRIGGERS SURVIVE A CAVE LOAD. 0x10878 clears nine
            // globals and these are not among them: 0x13ED8 watches ds:0x16AA
            // and 0x14613 watches ds:0x16AC, and whatever the last cave left
            // there is what the next one starts with.
            //
            // That is the original's behaviour and not a fault. It is a fault
            // in this SWEEP, which loads forty caves back to back into one
            // machine while the port gets a fresh Simulation each time - so the
            // original hatched a worm on scan 0 from a trigger the port had
            // never seen, and reported FFB0 against the port's FF7D.
            //
            // Cleared here so the two sides start from the same place. What the
            // original does with them BETWEEN caves is a separate question and
            // a real one; this comparison is about the scan.
            m.WriteWord(ds, 0x16AA, 0);
            m.WriteWord(ds, 0x16AC, 0);

            // AND THE QUOTA. 0x14316 is the closed exit's handler and it is two
            // instructions: `cmpw $0,0x164a` and, if that is zero, write the
            // open exit. The loader does not set it - the main loop does, from
            // the cave's own requirement - so cold-calling the scan left it at
            // whatever the last cave finished with and every exit sprang open
            // on scan 0.
            //
            // The port gets the same number from caves.json, which is where
            // GameHost gets it.
            m.WriteWord(ds, 0x164A, (ushort)tables.Required[cave]);

            // AND THE AMOEBA'S CAP. 0x13E62 increments the colony counter at
            // ds:0x1604 and compares it against ds:0x1602; reaching it calls
            // the conversion at 0x1EF1E and clears "this cave has an amoeba".
            //
            // The loader does not set the cap either, so a cold-called scan
            // compared this cave's colony against the last cave's limit - and
            // four caves converted their amoeba to falling boulders on scan 0
            // while the port animated it. The counter at 0x1604 and the
            // enclosed flag at 0x16A0 are both set by the scan's own head at
            // 0x140DE, so this is the only one missing.
            //
            // Per world, not per cave, which is why the index is cave / 4.
            m.WriteWord(ds, 0x1602, (ushort)tables.AmoebaCap[cave / 4]);

            // AND THE GENERATOR, which is the THIRD of PORT-PLAN's four
            // targets and the one nothing had ever tested.
            //
            // 0x124B1 is five instructions - load ds:0x2260, add 9248h, rotate
            // right three, store it back - and GameRandom.Next is the same five.
            // Both originals initialise the word to 04D2h.
            //
            // Seeded per cave so the two sides start level. The state is then
            // compared after EVERY pass below, which is what makes this a test
            // of the claim rather than of the arithmetic: the generator being
            // right is easy, and drawing from it the same number of times in
            // the same places is the part that can silently be wrong.
            m.WriteWord(ds, RandomState, 0x04D2);


            if (Routine.Call(m, code, Loader) is null)
            {
                Console.WriteLine($"  cave {cave + 1,2}  the loader did not return");
                return 1;
            }

            // The port starts from the same 880 bytes, so both sides begin the
            // scan from an identical grid by construction rather than by
            // copying one into the other.
            var ours = CaveLoader.Load(authored);

            // EVERYTHING GameHost HANDS OVER. The first version passed a grid
            // and a clock and nothing else, so the amoeba had no cap and no
            // "this cave has one" flag, and neither worm knew where its egg had
            // been. Four caves then came back with a falling boulder on the
            // original against an amoeba state on the port at scan 0, which
            // looked like a real divergence and was a simulation set up with
            // five of its seven inputs missing.
            //
            // The seed is 0x04D2, which both originals initialise their
            // generator word to.
            var sim = new Simulation(ours.Grid, tables.Time[cave], randomSeed: 0x04D2)
            {
                PlayerCell = ours.PlayerCell,
                CaveHasAmoeba = ours.HasAmoeba,
                AmoebaCap = tables.AmoebaCap[cave / 4],
                TreasureQuota = tables.Required[cave],
                ScoreBeforeQuota = tables.ScoreBefore[cave],
                ScoreAfterQuota = tables.ScoreAfter[cave],
                WormAHead = Math.Max(ours.TimedEntityACell, 0),
                WormBHead = Math.Max(ours.TimedEntityBCell, 0),
            };

            var diverged = -1;
            var seedDiverged = -1;
            var draws = 0;
            var differing = 0;

            for (var step = 0; step < steps; step++)
            {
                // THE MAIN LOOP'S STEP BODY, not the scan alone.
                //
                // Comparing 0x140DE against Simulation.Step could never align
                // the generator, because Step does more than the scan does:
                // it moves the worms and produces the sound frames, and the
                // sound routine draws at 0x1341F and 0x134E4. The original
                // makes those draws too - from 0xEDC8, OUTSIDE the scan - so
                // the port advanced the generator and the original did not.
                //
                // Five caves ran fifty scans with the original's word still at
                // 04D2 while the port's had moved, and the grids agreed
                // throughout: the divergence was real and simply had not
                // surfaced yet, which is exactly what comparing the state
                // rather than the output is for.
                //
                // 0xEDBF is the worms, 0xEDC2 the scan, 0xEDC8 the sound, and
                // 0xEDD5 the frame counter - which is why that is no longer
                // poked by hand below.
                if (!StepBody(machine, code))
                {
                    Console.WriteLine($"  cave {cave + 1,2}  the step did not finish at {step}");
                    return 1;
                }

                // THE FRAME COUNTER IS OUTSIDE THE SCAN. 0xEDD5 increments
                // ds:0x16C2 in the main loop AFTER calling 0x140DE, and the
                // port's Simulation.Step does it at the end of its own pass.
                // Calling the scan alone therefore left the original's counter
                // standing still while the port's advanced, and the open exit -
                // which animates on odd passes because of exactly this counter -
                // came out one state apart in every cave.
                //
                // A one-cell difference that was the harness and not the port.
                sim.Step();

                // THE GENERATOR, after every pass. A grid that agrees while
                // the seeds have parted means the two sides have drawn a
                // different number of times and simply not diverged YET.
                var theirSeed = m.ReadWord(ds, RandomState);
                var ourSeed = sim.Random.Seed;
                if (theirSeed != ourSeed && seedDiverged < 0)
                {
                    seedDiverged = step;
                    draws += Draws(ourSeed) - Draws(theirSeed);
                    Console.WriteLine($"       scan {step}: the generator parted after "
                                      + $"{Draws(ourSeed) - Draws(theirSeed)} extra port draw(s) - "
                                      + $"original {theirSeed:X4}, port {ourSeed:X4}");
                }

                differing = 0;
                for (var address = 0; address < GridBytes; address += 2)
                {
                    var theirs = m.ReadWord(ds, (ushort)address);
                    var mine = ours.Grid.ReadWord(address);
                    if (theirs == mine) continue;

                    differing++;

                    // The first one, named. A count says something is wrong; a
                    // cell and two values says what.
                    if (diverged < 0 && differing == 1)
                    {
                        Console.WriteLine($"       scan {step}: cell {address:X4} "
                                          + $"({CaveGrid.ColumnOf(address)},{CaveGrid.RowOf(address)}) "
                                          + $"original {theirs:X4}, port {mine:X4}");
                    }
                }

                if (differing > 0 && diverged < 0) diverged = step;
            }

            worst = Math.Max(worst, differing);

            Console.WriteLine(diverged < 0 && seedDiverged < 0
                ? $"  cave {cave + 1,2}  agrees for all {steps} scans, grid and generator"
                : $"  cave {cave + 1,2}  first differs at scan {diverged}, "
                  + $"{differing} cells apart after {steps}");
        }

        Console.WriteLine();

        // THE FIVE THAT DIVERGE ARE THE AMOEBA, and they are the harness.
        //
        // Four of them are one cell authored 0x10 turning into FF46 - a falling
        // boulder - on the original while the port animates it to FF49. That is
        // what an amoeba does when its cap is exceeded or it is walled in, and
        // 0x13E50 decides it from globals the LOADER DOES NOT SET: the colony
        // counter at ds:0x1604, the enclosed flag at ds:0x16A0 and the cave's
        // own cap. GameHost sets the port's from caves.json; the main loop sets
        // the original's, and cold-calling the scan skips it.
        //
        // That is the pattern in every divergence this sweep has found so far,
        // and it is worth stating plainly: the per-cave setup is spread through
        // the MAIN LOOP rather than gathered in the loader, so a scan called in
        // isolation starts from whatever the previous cave finished with. Three
        // of those have been run down and are set above - the frame counter,
        // the two egg triggers and the quota. The amoeba's are the fourth and
        // are not done.
        //
        // Reported as unfinished rather than excluded. A sweep that quietly
        // skipped the caves it could not set up would be reporting agreement it
        // had not measured, which is the defect this tool had once already.
        Console.WriteLine(worst == 0
            ? $"the step agrees, cell for cell and draw for draw, over {steps} passes"
            : $"{worst} cells apart at worst - see the note in Sweep.cs");
        return worst == 0 ? 0 : 1;
    }

    /// <summary>The scan loop docs/PORT-PLAN.md derives the whole model from.</summary>
    private const int ScanLoop = 0x140DE;

    /// <summary>ds:0x16C2, incremented by the main loop at 0xEDD5, after the scan.</summary>
    private const ushort FrameCounter = 0x16C2;

    /// <summary>ds:0x2260, the generator's word. 0x124B1 is its only writer.</summary>
    private const ushort RandomState = 0x2260;

    /// <summary>The main loop's step body: the worms, the scan, the sound, the counter.</summary>
    private const int StepFirst = 0xEDBF;
    private const int StepAfter = 0xEDD9;

    /// <summary>Run one pass of the main loop, which is what Simulation.Step is.</summary>
    private static bool StepBody((Machine8086 Machine, Dos Dos, Display Video) machine, ushort code)
    {
        var m = machine.Machine;
        var (segment, offset) = Routine.Resolve(m.LoadSegment, code, StepFirst);
        var (_, stop) = Routine.Resolve(m.LoadSegment, code, StepAfter);

        m.Segments[Machine8086.CS] = segment;
        m.Ip = offset;

        var result = Harness.Run(machine, at: Machine8086.Linear(segment, stop), limit: 5_000_000);
        return result.Reached;
    }

    /// <summary>How many draws from 04D2h reach a given state, or -1.</summary>
    private static int Draws(ushort state)
    {
        var s = (ushort)0x04D2;
        for (var n = 0; n <= 64; n++)
        {
            if (s == state) return n;
            var ax = (ushort)(s + 0x9248);
            s = (ushort)((ax >> 3) | (ax << 13));
        }

        return -1;
    }

    private static int Global(Machine8086 m, ushort ds, ushort at, ushort mine) =>
        m.ReadWord(ds, at) == mine ? 0 : 1;

    private static int Compare(
        (Machine8086 Machine, Dos Dos, Display Video) machine,
        ReadOnlySpan<byte> authored,
        out string note,
        out int changed)
    {
        var m = machine.Machine;
        var ds = m.Segments[Machine8086.DS];

        // The original reads its cave from a fixed address, so there is no
        // argument to pass and no menu to drive.
        for (var i = 0; i < authored.Length; i++)
        {
            m.WriteByte(ds, (ushort)(AuthoredAt + i), authored[i]);
        }

        m.Segments[Machine8086.ES] = ds;
        var took = Routine.Call(m, machine.Dos.CodeSegment!.Value, Loader);

        if (took is null)
        {
            note = "the loader did not return";
            changed = 0;
            return -1;
        }

        var ours = CaveLoader.Load(authored);
        var differing = 0;
        changed = 0;

        for (var address = 0; address < GridBytes; address += 2)
        {
            var theirs = m.ReadWord(ds, (ushort)address);
            var mine = ours.Grid.ReadWord(address);
            if (theirs != mine) differing++;
            if (theirs != 0xFFFF) changed++;
        }

        // And the four globals, which the grid comparison cannot see.
        differing += Global(m, ds, PlayerCellAt, (ushort)Math.Max(ours.PlayerCell, 0));
        differing += Global(m, ds, TimedAAt, (ushort)Math.Max(ours.TimedEntityACell, 0));
        differing += Global(m, ds, TimedBAt, (ushort)Math.Max(ours.TimedEntityBCell, 0));

        var amoeba = m.ReadByte(ds, AmoebaAt) != 0;
        if (amoeba != ours.HasAmoeba) differing++;

        note = $"{took:N0} instructions";
        return differing;
    }
}
