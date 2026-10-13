using Rockford.Reference;

namespace Rockford.Tests;

/// <summary>
/// The harness that runs the original's own code, checked against things this
/// repository already knew by other means.
/// </summary>
/// <remarks>
/// <para>
/// A comparison harness has a problem no ordinary component has: if it is
/// wrong, everything it reports is wrong in a way that looks like evidence. So
/// it cannot be tested against itself, and it cannot be tested against my
/// reading of the disassembly, because checking a reading is what it is for.
/// </para>
/// <para>
/// It is tested here against facts this repository established independently,
/// years of 8086 documentation ago: the palette block <c>docs/FORMATS.md</c>
/// located at <c>0x04093</c> by reading the executable, and the picture
/// <c>tools/ega.py</c> decodes out of <c>TITLE.FIL</c> in Python with no 8086
/// anywhere. If the interpreter arrives at both by running instructions, the
/// machine underneath is doing what a machine does.
/// </para>
/// </remarks>
public class ReferenceTests(ReferenceTests.Title title) : IClassFixture<ReferenceTests.Title>
{
    /// <summary>
    /// One boot, run to the title screen, shared by every test that only looks
    /// at it.
    /// </summary>
    /// <remarks>
    /// Five separate boots took 49 seconds between them. The run is
    /// deterministic, so five of them are five copies of one answer.
    /// </remarks>
    public sealed class Title
    {
        public (Machine8086 Machine, Dos Dos, Display Video)? Machine { get; }

        public Title()
        {
            var volume = Volume();
            if (volume is null) return;

            var machine = Harness.BootFrom(volume);
            machine.Dos.Type("N");

            // STOP THE MOMENT THE SECOND FILE IS OPENED, not after a fixed
            // number of instructions. TITLE.FIL is the still screen and
            // TITLEANI.FIL is the animation that then draws over it, so a
            // budget that is comfortably large enough to paint the title is
            // also large enough to start spoiling it.
            //
            // It did, as soon as the timer started ticking: this comparison
            // passed against a frozen screen for one commit and then failed
            // against a moving one, which is a fixture that was measuring the
            // instruction budget rather than the game.
            Harness.Run(machine, limit: 2_000_000,
                until: _ => machine.Dos.Opened.Count >= 2);
            Machine = machine;
        }
    }

    /// <summary>The two extracted EGA disks, or null when they are not here.</summary>
    private static string[]? Volume() => Disks("ega1", "ega2");

    /// <summary>
    /// The extracted VGA disk, which is the release the port was written
    /// against.
    /// </summary>
    /// <remarks>
    /// The routine addresses cited in <c>docs/PORT-PLAN.md</c> and in
    /// <c>src/Rockford.Engine</c> are VGA file offsets, and
    /// <c>unpacked/ega.bin</c> holds twelve zero bytes at all six of the ones
    /// <c>tools/cicodis-check.py</c> asserts - so the EGA build cannot be
    /// compared against the documented simulation.
    ///
    /// Not EVERY address in those documents: <c>PORT-PLAN.md</c> line 581
    /// cites <c>0x1FAF3</c> as an EGA routine and it is there. Both builds are
    /// run because they exercise the display two completely different ways.
    /// </remarks>
    private static string[]? VgaVolume() => Disks("vga");

    /// <summary>Whichever release is present, for tests that need only a disk.</summary>
    internal static string[]? AnyVolume() => Volume() ?? VgaVolume();

    private static string[]? Disks(params string[] names)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (!File.Exists(Path.Combine(d.FullName, "Rockford.slnx"))) continue;

            var paths = names.Select(n => Path.Combine(d.FullName, "extracted", n)).ToArray();
            return File.Exists(Path.Combine(paths[0], "ROCKFORD.EXE")) ? paths : null;
        }

        return null;
    }

    /// <summary>The title screen palette, as the executable holds it.</summary>
    /// <remarks>
    /// Read out of <c>unpacked/ega.bin</c> at <c>0x04093</c>, which
    /// <c>tools/egapal.py</c> identified statically. Repeated here rather than
    /// re-read so that the test fails if either one moves.
    /// </remarks>
    private static readonly byte[] TitlePalette =
        [0x00, 0x01, 0x14, 0x11, 0x14, 0x16, 0x04, 0x05,
         0x06, 0x06, 0x00, 0x10, 0x07, 0x07, 0x17, 0x17];

    [SkippableFact]
    public void TheProgramLoadsAndUnpacksItself()
    {
        var volume = Volume();
        Skip.If(volume is null, "no extracted EGA disks");

        var machine = Harness.BootFrom(volume!);

        // DOS's own arithmetic: the header asks for 0x22FB paragraphs of stack
        // segment and 7122 of minimum allocation, and the entry point is at
        // 0718:0012 relative to the load segment.
        Assert.Equal(Dos.LoadSegment + 0x0718, machine.Machine.Segments[Machine8086.CS]);
        Assert.Equal(0x0012, machine.Machine.Ip);

        var result = Harness.Run(machine, limit: 3_000);

        // The EXEPACK stub relocates the image and jumps to the real entry,
        // which is in a completely different segment from the one DOS loaded.
        Assert.Null(result.Fault);
        Assert.NotEqual(Dos.LoadSegment + 0x0718, machine.Machine.Segments[Machine8086.CS]);
    }

    [SkippableFact]
    public void ItAsksAboutTheJoystickAndTakesAnAnswer()
    {
        var volume = Volume();
        Skip.If(volume is null, "no extracted EGA disks");

        var machine = Harness.BootFrom(volume!);
        Harness.Run(machine, limit: 400_000);

        // The gate every run has to get through, and the reason a first boot
        // looks like it is running when it has stalled.
        Assert.Contains("joystick", machine.Dos.Teletype.ToString());
        Assert.True(machine.Dos.KeyWaits > 0);
    }

    [SkippableFact]
    public void ItReachesTheTitleScreen()
    {
        Skip.If(title.Machine is null, "no extracted EGA disks");
        var machine = title.Machine!.Value;

        // Mode 0Dh: 320x200 in sixteen colours, four planes of 8,000 bytes -
        // which is why a .FIL is 32,000 bytes long.
        Assert.Equal([0x0D], machine.Dos.Modes);
        Assert.Contains("TITLE.FIL", machine.Dos.Opened);
    }

    [SkippableFact]
    public void TheRunArrivesAtThePaletteTheExecutableHolds()
    {
        Skip.If(title.Machine is null, "no extracted EGA disks");

        // Established by reading in tools/egapal.py, arrived at here by
        // running. The first thing in this repository proved both ways.
        Assert.Equal(TitlePalette, title.Machine!.Value.Video.Attributes[..16]);
    }

    [SkippableFact]
    public void TheTitleScreenMatchesTheDecoderPixelForPixel()
    {
        Skip.If(title.Machine is null, "no extracted EGA disks");

        var ours = title.Machine!.Value.Video.Pixels();
        var theirs = Planar(File.ReadAllBytes(Path.Combine(Volume()![0], "TITLE.FIL")));

        // Not "close". The original read that file through this DOS and wrote
        // it into the planes through the latches, the map mask and whichever
        // write mode it chose, and the result has to be the same picture
        // tools/ega.py gets by unpacking the bitplanes in Python with no 8086
        // anywhere near it.
        var differing = ours.Where((p, i) => p != theirs[i]).Count();
        Assert.Equal(0, differing);
    }

    [SkippableFact]
    public void AndTheTitleScreenProvesLessThanItLooksLike()
    {
        Skip.If(title.Machine is null, "no extracted EGA disks");
        var video = title.Machine!.Value.Video;

        // WHAT THAT MATCH IS WORTH, measured rather than hoped.
        //
        // The honest way to read a passing comparison is to break the model on
        // purpose and see whether it notices. Doing that gives, on this path:
        //
        //   ignore the map mask      51,814 pixels differ - CAUGHT
        //   break the replace        8,000 pixels differ  - CAUGHT
        //   ignore the bit mask      0 differ             - NOT CAUGHT
        //   ignore data rotate       0 differ             - NOT CAUGHT
        //   stop storing latches     0 differ             - NOT CAUGHT
        //
        // So the title comparison establishes the plane selection, the write
        // mode and the pixel layout, and establishes nothing whatever about
        // masking, rotation or the latches. The counters below are the scope
        // of the claim, written down so the day a path uses more of the card
        // the difference is visible rather than assumed.
        //
        // They are exact, not floors. A floor would absorb exactly the kind of
        // change that matters: these numbers moved once already, when the
        // fixture stopped being a fixed instruction budget, and again when
        // `out dx,ax` started reaching both halves of an index/data pair and
        // the game's real blitter came to life.
        Assert.Equal(40_416, video.WriteModeUses[0]);
        Assert.Equal(40_416, video.FunctionUses[0]);

        // 32,000 of those are TITLE.FIL itself - four planes of 8,000, poked
        // one plane at a time through the map mask, which is what makes a
        // .FIL the shape it is.
        Assert.Equal(32_000, video.MaskedPlanes);

        // 116 writes go through a bit mask other than FFh - the BIOS teletype
        // drawing the joystick prompt, which is the only thing on this path
        // that masks anything. It was ZERO before `out dx,ax` was fixed, and
        // the number is here rather than a "greater than zero" because 116 is
        // small enough that a regression could easily leave it non-zero and
        // wrong.
        Assert.Equal(116, video.MaskedWrites);

        // 120 latch loads against 40,416 writes: the latches are touched and
        // are nowhere near being exercised.
        Assert.Equal(120, video.Latched);

        // Zero on this path, and that is the limitation, not a result. The
        // test below covers them.
        Assert.Equal(0, video.SetResetWrites);
        Assert.Equal(0, video.WriteModeUses[1]);
        Assert.Equal(0, video.WriteModeUses[2]);
    }

    [SkippableFact]
    public void ButTheRUNNINGGameUsesTheRestOfTheCard()
    {
        var volume = Volume();
        Skip.If(volume is null, "no extracted EGA disks");

        var machine = Harness.BootFrom(volume!);
        machine.Dos.Type("N");
        Harness.Run(machine, limit: 2_000_000);

        // WHAT THIS DOES ESTABLISH: that the game's blitter runs at all. Every
        // one of these was ZERO until `out dx,ax` was fixed to write both
        // halves of an index/data pair - the EGA build has that sequence at
        // 0x21400, so the map mask stayed at 0Fh where the program had asked
        // for 01h and the tile copy never happened.
        //
        // WHAT IT DOES NOT: prove the card is right. It counts operations, and
        // a counter cannot tell a correct latch copy from an incorrect one.
        // Breaking the bit mask, the rotation, the latch store or the mode-1
        // copy all leave this passing. Those are covered by
        // ReferenceHardwareTests, which checks results rather than tallies.
        //
        // The distinction is the whole lesson: a sweep that matches is worth
        // what the original was doing while it matched, and a count of what it
        // did is not the same as a check of what it produced.
        Assert.True(machine.Video.WriteModeUses[1] > 20_000,
            $"write mode 1, the latch copy: {machine.Video.WriteModeUses[1]}");
        Assert.True(machine.Video.Latched > 20_000,
            $"latch loads: {machine.Video.Latched}");
    }

    [SkippableFact]
    public void TheGameInstallsItsOwnTimerAndTheClockRuns()
    {
        var volume = Volume();
        Skip.If(volume is null, "no extracted EGA disks");

        var machine = Harness.BootFrom(volume!);
        machine.Dos.Type("N");
        var result = Harness.Run(machine, limit: 3_000_000);

        Assert.Null(result.Fault);

        // THE REGRESSION THIS EXISTS FOR. An empty interrupt table answers
        // zero, the game's saved segment starts at zero, and its "have I
        // already hooked the timer" test at 0x21392 therefore said yes. It
        // skipped the install and sat in its frame pacer for ever.
        Assert.Contains((byte)0x08, machine.Dos.Hooked);
        Assert.True(machine.Machine.HookedByProgram(0x08),
            "INT 8 must point into the program, not at the BIOS stub");

        // And the interrupt is actually delivered, which nothing did until
        // the pacer was found waiting on it.
        Assert.True(machine.Machine.RaisedCounts.GetValueOrDefault(8) > 100);
    }

    [SkippableFact]
    public void ItProgramsTheTimerToSixtyHertz()
    {
        var volume = Volume();
        Skip.If(volume is null, "no extracted EGA disks");

        var machine = Harness.BootFrom(volume!);
        machine.Dos.Type("N");
        Harness.Run(machine, limit: 3_000_000);

        // docs/PORT-PLAN.md says "The interrupt runs at 60.0 Hz, from PIT
        // divisor 0x4DAE", established by reading. Here the program writes it.
        Assert.Equal(0x4DAE, machine.Machine.Pit.Divisor);
    }

    [SkippableFact]
    public void TheVgaBuildDecompressesItsTitleCorrectlyToo()
    {
        var volume = VgaVolume();
        Skip.If(volume is null, "no extracted VGA disk");

        var machine = Harness.BootFrom(volume!);
        machine.Dos.Type("N");
        var result = Harness.Run(machine, limit: 5_000_000,
            until: _ => machine.Dos.Opened.Count >= 2);

        Assert.Null(result.Fault);

        // Mode 13h: one byte a pixel, no planes and no latches, which is a
        // completely different path through the card from the EGA build.
        Assert.Equal([0x13], machine.Dos.Modes);
        Assert.True(machine.Video.Linear);
        Assert.Contains("TITLE.SCM", machine.Dos.Opened);

        // And a harder claim than the EGA one. A .FIL is the screen; a .SCM is
        // RLE and has to be decompressed by the game's own nine-instruction
        // decoder at 0x1275B, running on this interpreter, against
        // tools/scmformat.py doing the same job in Python.
        var ours = machine.Video.Pixels();
        var theirs = Scm(File.ReadAllBytes(Path.Combine(volume![0], "TITLE.SCM")));

        var differing = ours.Where((p, i) => p != theirs[i]).Count();
        Assert.Equal(0, differing);
    }

    [SkippableFact]
    public void TheTimerHandlerDoesNotClobberTheProgram()
    {
        var volume = VgaVolume();
        Skip.If(volume is null, "no extracted VGA disk");

        var machine = Harness.BootFrom(volume!);
        machine.Dos.Type("N");

        // THE REGRESSION. The BIOS timer stub wrote `mov al,20h` for the
        // end-of-interrupt without saving AX. The .SCM decoder reads a control
        // byte into AL and branches on it, so an interrupt landing between the
        // read and the branch turned a run of two into a literal run of 32 -
        // 20h - and put the compressed stream on screen as pixels.
        //
        // The pixel test above is what catches it. This one says WHY, so that
        // a future failure there is not investigated as a decoder fault.
        //
        // THE FIRST VERSION OF THIS TEST COULD NOT FAIL. It asserted a
        // LIFETIME interrupt count, which says nothing about whether one
        // landed during the decode: delivering a handful before the title and
        // none at all during decompression satisfies it, and the picture then
        // matches for the wrong reason. So count them where it matters - the
        // decoder's own loop head, at file offset 0x1275B, which is
        // CS:0x275B once the image is at its load segment.
        var atEntry = -1L;
        var atExit = 0L;
        var visits = 0;

        Harness.Run(machine, limit: 5_000_000,
            until: _ => machine.Dos.Opened.Count >= 2,
            observeAt: Machine8086.Linear(
                (ushort)(Dos.LoadSegment + 0x1000), 0x275B),
            observe: m =>
            {
                if (atEntry < 0) atEntry = m.RaisedCounts.GetValueOrDefault(8);
                atExit = m.RaisedCounts.GetValueOrDefault(8);
                visits++;
            });

        Assert.True(visits > 1_000, $"the decoder loop must run: {visits} visits");
        Assert.True(atExit - atEntry > 0,
            $"the timer must fire DURING the decode, or the test above proves "
            + $"nothing: {atExit - atEntry} interrupts across {visits} visits");
    }

    [SkippableFact]
    public void PausingARunDoesNotChangeWhatIsDelivered()
    {
        var volume = VgaVolume();
        Skip.If(volume is null, "no extracted VGA disk");

        // One run of 400,000 instructions against four of 100,000. The
        // delivery bookkeeping used to be LOCAL to the run loop, so every
        // resume forgot which wrap had been served and two short runs lost an
        // interrupt that one long run delivered.
        //
        // A reference whose answer depends on where you paused it cannot be
        // used to explain a difference, because the difference might be the
        // pause.
        var whole = Harness.BootFrom(volume!);
        whole.Dos.Type("N");
        Harness.Run(whole, limit: 400_000);

        var pieces = Harness.BootFrom(volume!);
        pieces.Dos.Type("N");
        for (var i = 0; i < 4; i++) Harness.Run(pieces, limit: 100_000);

        Assert.Equal(whole.Machine.Executed, pieces.Machine.Executed);
        Assert.Equal(whole.Machine.RaisedCounts.GetValueOrDefault(8),
                     pieces.Machine.RaisedCounts.GetValueOrDefault(8));
        Assert.Equal(whole.Video.Pixels(), pieces.Video.Pixels());
    }

    [SkippableFact]
    public void TheKeyboardKeepsDeliveringAfterTheGameChainsToTheBios()
    {
        var volume = VgaVolume();
        Skip.If(volume is null, "no extracted VGA disk");

        var machine = Harness.BootFrom(volume!);
        machine.Dos.Type("N");

        var keys = new Queue<(long, byte)>();
        for (var i = 0; i < 8; i++)
        {
            keys.Enqueue((200 + i * 20, 0x1C));          // enter, down
            keys.Enqueue((201 + i * 20, 0x9C));          // and up
        }

        Harness.Run(machine, limit: 12_000_000, keys: keys);

        // THE DEADLOCK. The game's own INT 9 handler chains to the BIOS
        // through a far call at 0x11919. While the BIOS stub was a bare IRET
        // it never wrote the end-of-interrupt, so the controller stayed in
        // service and NO SECOND KEY WAS EVER DELIVERED - one keyboard
        // interrupt and 43 timer interrupts in three million instructions.
        //
        // Both counts matter. The keys have to keep arriving, and the timer
        // has to keep arriving alongside them rather than being starved by a
        // queue that is permanently overdue.
        Assert.True(machine.Machine.RaisedCounts.GetValueOrDefault(9) > 8,
            $"keys delivered: {machine.Machine.RaisedCounts.GetValueOrDefault(9)}");
        Assert.True(machine.Machine.RaisedCounts.GetValueOrDefault(8) > 1_000,
            $"timer interrupts: {machine.Machine.RaisedCounts.GetValueOrDefault(8)}");
    }

    [SkippableFact]
    public void TheCaveLoaderAgreesWithTheOriginalOnEveryCaveAndEveryByte()
    {
        var volume = VgaVolume();
        Skip.If(volume is null, "no extracted VGA disk");

        var machine = Harness.BootFrom(volume!);
        machine.Dos.Type("N");
        Harness.Run(machine, limit: 2_000_000, until: _ => machine.Dos.Opened.Count >= 1);

        var m = machine.Machine;
        var ds = m.Segments[Machine8086.DS];
        var cellmaps = File.ReadAllBytes(Path.Combine(volume![0], "CELLMAPS.BIN"));
        var caves = cellmaps.Length / Rockford.Engine.CaveLoader.RecordSize;

        Assert.Equal(40, caves);

        var records = new List<byte[]>();
        for (var cave = 0; cave < caves; cave++)
        {
            records.Add(cellmaps[(cave * 880)..((cave + 1) * 880)]);
        }

        // Every authored byte value, not only the 25 the shipped caves use.
        // The port's switch has 26 arms, so 2D and the default - whose own
        // comment says "no shipped cave reaches this" - had nothing behind
        // them but a reading of the listing.
        for (var pass = 0; pass < 4; pass++)
        {
            var synthetic = new byte[880];
            for (var i = 0; i < synthetic.Length; i++)
            {
                synthetic[i] = (byte)((pass * 880 + i) % 256);
            }

            records.Add(synthetic);
        }

        var work = 0;

        foreach (var authored in records)
        {
            // The original reads its cave from a fixed address and takes no
            // arguments, so there is no menu to drive and no file to load.
            for (var i = 0; i < authored.Length; i++)
            {
                m.WriteByte(ds, (ushort)(0x3010 + i), authored[i]);
            }

            // The prefill and the decode both write through ES.
            m.Segments[Machine8086.ES] = ds;

            var took = Routine.Call(m, machine.Dos.CodeSegment!.Value, 0x10878);
            Assert.NotNull(took);

            var ours = Rockford.Engine.CaveLoader.Load(authored);

            for (var address = 0; address < 22 * 0x100; address += 2)
            {
                var theirs = m.ReadWord(ds, (ushort)address);
                Assert.Equal(ours.Grid.ReadWord(address), theirs);
                if (theirs != 0xFFFF) work++;
            }

            // And the four things it records besides the grid.
            Assert.Equal(m.ReadWord(ds, 0x16BE), (ushort)Math.Max(ours.PlayerCell, 0));
            Assert.Equal(m.ReadWord(ds, 0x1614), (ushort)Math.Max(ours.TimedEntityACell, 0));
            Assert.Equal(m.ReadWord(ds, 0x161C), (ushort)Math.Max(ours.TimedEntityBCell, 0));
            Assert.Equal(m.ReadByte(ds, 0x166A) != 0, ours.HasAmoeba);
        }

        // WHAT THE MATCH IS WORTH. Most of the grid is the FFFF prefill and
        // agrees whatever either side does. This is the part the original
        // wrote something else into, and a sweep is worth exactly as much.
        Assert.True(work > 38_000, $"the original only did real work on {work} cells");
    }

    /// <summary>
    /// Raised where <c>tools/scmformat.py</c> raises <c>ScmError</c>.
    /// </summary>
    public sealed class ScmException(string message) : Exception(message);

    /// <summary>
    /// <c>tools/scmformat.py</c>'s run-length decode, in C#.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>80h</c> ends the stream, <c>00h</c> does nothing, a byte with the
    /// top bit set is a run of that many and anything else is that many
    /// literals.
    /// </para>
    /// <para>
    /// <b>AND IT REJECTS WHAT THE PYTHON REJECTS.</b> The first version
    /// returned a zero-padded screen on an early terminator, silently clipped
    /// a run or a literal that overshot 64,000, and accepted a literal that
    /// ran off the end of the file. Every one of those is an <c>ScmError</c>
    /// in <c>scmformat.py</c>.
    /// </para>
    /// <para>
    /// That mattered more than it looks. This method is the ORACLE - the
    /// thing the interpreter is checked against - and an oracle that is wrong
    /// in the same direction as the code it checks turns "0 of 64,000 pixels
    /// differ" into a statement about nothing. The shipped comparison was
    /// still valid, because no shipped file is malformed; the claim that the
    /// two decoders agree was not.
    /// </para>
    /// </remarks>
    private static byte[] Scm(byte[] file, int limit = Display.Width * Display.Height)
    {
        var pixels = new byte[limit];
        var read = 0;
        var written = 0;

        while (written < limit)
        {
            if (read >= file.Length)
            {
                throw new ScmException(
                    $"stream ended after {written} pixels, expected {limit}");
            }

            var control = file[read++];

            if (control == 0x80)
            {
                throw new ScmException(
                    $"terminator at {read - 1} after {written} pixels, expected {limit}");
            }

            if (control == 0x00) continue;

            if ((control & 0x80) != 0)
            {
                var run = control & 0x7F;
                if (read >= file.Length)
                {
                    throw new ScmException($"run of {run} at {read - 1} has no value byte");
                }

                if (written + run > limit)
                {
                    throw new ScmException(
                        $"run of {run} at {read - 1} overshoots {limit} "
                        + $"by {written + run - limit}");
                }

                var value = file[read++];
                for (var i = 0; i < run; i++) pixels[written++] = value;
            }
            else
            {
                if (read + control > file.Length)
                {
                    throw new ScmException(
                        $"literal of {control} at {read - 1} runs "
                        + $"{read + control - file.Length} past the end");
                }

                if (written + control > limit)
                {
                    throw new ScmException(
                        $"literal of {control} at {read - 1} overshoots {limit} "
                        + $"by {written + control - limit}");
                }

                for (var i = 0; i < control; i++) pixels[written++] = file[read++];
            }
        }

        return pixels;
    }

    /// <summary>
    /// The oracle has to refuse what the Python refuses.
    /// </summary>
    /// <remarks>
    /// Four cases, each one a thing the first version accepted. They are
    /// synthetic and need no game data, which is deliberate: the decoder
    /// equivalence is the weakest joint in the pixel evidence and it should
    /// not be one of the claims that silently skips on a clean checkout.
    /// </remarks>
    [Theory]
    [InlineData(new byte[] { 0x80 }, "terminator")]
    [InlineData(new byte[] { 0x02, 0x11, 0x22 }, "stream ended")]
    [InlineData(new byte[] { 0x84, 0x11 }, "overshoots")]
    [InlineData(new byte[] { 0x04, 0x11, 0x22 }, "past the end")]
    public void TheScmOracleRefusesWhatThePythonRefuses(byte[] stream, string because)
    {
        // A three-pixel screen, so a malformed stream can be four bytes long
        // instead of sixty-four thousand.
        var refused = Assert.Throws<ScmException>(() => Scm(stream, limit: 3));
        Assert.Contains(because, refused.Message);
    }

    [SkippableFact]
    public void TheOraclesDecodeEveryShippedScreen()
    {
        var vga = VgaVolume();
        var ega = Volume();
        Skip.If(vga is null && ega is null, "no extracted disks");

        var decoded = 0;

        foreach (var file in Directory.EnumerateFiles(vga?[0] ?? ega![0], "*.SCM"))
        {
            // Not "does not throw" as a comment - the method throws on every
            // malformation the Python rejects, so decoding all eight shipped
            // screens IS the claim that the oracle handles the real format and
            // not just the one file the comparison uses.
            Assert.Equal(Display.Width * Display.Height, Scm(File.ReadAllBytes(file)).Length);
            decoded++;
        }

        foreach (var directory in ega ?? [])
        foreach (var file in Directory.EnumerateFiles(directory, "*.FIL"))
        {
            Assert.Equal(Display.Width * Display.Height, Planar(File.ReadAllBytes(file)).Length);
            decoded++;
        }

        Assert.True(decoded >= 8, $"only {decoded} screens found");
    }

    [Fact]
    public void AndAcceptsWhatThePythonAccepts()
    {
        // The no-op the format allows and no shipped file uses, then a run of
        // two and one literal, landing exactly on the pixel count.
        Assert.Equal([0x77, 0x77, 0x55], Scm([0x00, 0x82, 0x77, 0x01, 0x55], limit: 3));
    }

    /// <summary>
    /// <c>tools/ega.py</c>'s planar decode, in C#: four bitplanes of 8,000
    /// bytes, one bit a pixel each, plane 0 least significant.
    /// </summary>
    private static byte[] Planar(byte[] file)
    {
        var pixels = new byte[Display.Width * Display.Height];

        for (var plane = 0; plane < 4; plane++)
        for (var i = 0; i < Display.PlaneLength; i++)
        {
            var b = file[plane * Display.PlaneLength + i];
            for (var bit = 0; bit < 8; bit++)
            {
                if ((b & 0x80 >> bit) != 0) pixels[i * 8 + bit] |= (byte)(1 << plane);
            }
        }

        return pixels;
    }
}
