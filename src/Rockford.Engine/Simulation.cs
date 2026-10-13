namespace Rockford.Engine;

/// <summary>
/// The cave simulation, ported from the scan loop at <c>0x140DE</c> and the handler
/// table at <c>0x13C50</c>.
///
/// Every step walks the cave top to bottom and left to right, dispatching on each
/// cell's value byte. Values below 4 are inert and never dispatch. That scan order
/// is load-bearing: an object that moves left has already been visited this step and
/// may act again, so it is written as a live state, while an object that moves right
/// has not been visited and is written as a deferred state so it cannot act twice.
/// The deferred states exist for no other reason.
/// </summary>
public sealed partial class Simulation
{
    /// <summary>Signed threshold on elapsed interrupt ticks, from <c>0x13F7E</c>.
    /// 3600 ticks at 60 Hz is 60 seconds.</summary>
    public const int TimedWallTicks = 0xE10;

    /// <summary>Interrupt ticks per simulation step, from the pacing wait at
    /// <c>0x13353</c>. The interrupt runs at 60 Hz, so the simulation runs at 10 Hz.</summary>
    public const int TicksPerStep = 6;

    private readonly CaveGrid _grid;
    private readonly GameRandom _random;

    // Globals, named for what they do. The original's address is in the comment so a
    // reader can go back to the disassembly.
    private int _interruptTicks;        // ds:0x16A8, incremented at 0x1337C
    private int _playerCell = -1;       // ds:0x16BE
    private int _playerDeathTimer;      // ds:0x1648, set to 8 at 0x11384
    private byte _soundFlags;           // ds:0x2262

    /// <summary>
    /// Set the first time anything falls into a timed converting wall, and never
    /// cleared. <c>ds:0x1696</c>.
    ///
    /// Settable because it outlives the cave - see <see cref="CarryGlobalsFrom"/>.
    /// </summary>
    public bool TimedWallLatched { get; set; }

    /// <summary>The interrupt tick the latch was set on. <c>ds:0x1CF4</c>, and it
    /// outlives the cave too, while the counter it is measured against does not.</summary>
    public int TimedWallStart { get; set; }

    /// <summary>
    /// Where the player was when the footstep last fired. <c>ds:0x1DF8</c>.
    ///
    /// Zero, which is what the executable's image holds - nothing initialises it
    /// at runtime and nothing else touches it. So the first tick of a cave finds
    /// the player somewhere other than address zero and sounds a footstep on
    /// arrival, before they have moved. That is the original's behaviour and not
    /// an accident of uninitialised memory.
    ///
    /// It outlives the cave, which is why it is settable: the original keeps it in
    /// a global that the loader at <c>0x1094B</c> and the respawn at <c>0xFE70</c>
    /// both leave alone, so a host building a fresh Simulation per cave has to
    /// carry it across. <see cref="CarryGlobalsFrom"/> does that.
    /// </summary>
    public int LastSoundedCell { get; set; }

    /// <summary>
    /// A cave, and the seconds on its clock.
    ///
    /// The time is required rather than a settable default, because a cave with
    /// no time is not a state the game can reach. Every authored cave carries one
    /// from the table at <c>0x26A2</c>: all forty entries are non-zero, running
    /// from 80 to 180 seconds, and the twenty the menu can reach run from 90 to
    /// 170.
    ///
    /// Left at zero the clock runs out after ten steps and kills the player, which
    /// is faithful and is a trap - a caller that forgot to set it got a cave that
    /// died a second in, for a reason that looks like anything but a forgotten
    /// argument.
    ///
    /// Note the order. The seconds come SECOND and the seed third, where the seed
    /// used to be second, so <c>new Simulation(g, 1)</c> has quietly changed
    /// meaning from seed one to one second. Nothing in the type system catches
    /// that - the clock is an int and the seed a ushort - so pass the seed by
    /// name.
    /// </summary>
    /// <param name="seconds">The cave's time limit. Zero is allowed and means
    /// exactly what it says: a cave the player cannot survive.</param>
    public Simulation(CaveGrid grid, int seconds, ushort randomSeed = 0)
    {
        _grid = grid;
        _random = new GameRandom(randomSeed);
        TimeRemaining = seconds;
    }

    public CaveGrid Grid => _grid;

    public GameRandom Random => _random;

    /// <summary>Interrupt ticks elapsed. One step advances it by <see cref="TicksPerStep"/>.</summary>
    public int InterruptTicks => _interruptTicks;

    /// <summary>True once a blast has covered the player's cell. The original does not
    /// end the cave there and then: it sets a countdown and restarts eight frames later,
    /// which is why the explosion is visible before the cave resets.</summary>
    public bool PlayerDead => _playerDeathTimer > 0;

    /// <summary>Frames left before the cave restarts, or zero. <c>ds:0x1648</c>.</summary>
    public int PlayerDeathTimer => _playerDeathTimer;

    /// <summary>
    /// True on the one step where the death countdown reaches zero, from
    /// <c>0xEE7D</c>. The host reads this to take a life and reload the cave.
    ///
    /// It is a single-step pulse rather than a latch because the countdown is the
    /// only thing that ends a cave badly, and a latch would need clearing by
    /// somebody - which is exactly the kind of state the original does not keep.
    /// </summary>
    public bool DeathComplete { get; private set; }

    /// <summary>
    /// Counts the death timer down, from <c>0xEE76</c>. The original runs this in
    /// its main loop rather than in the cell scan, before the scan, so a death that
    /// lands on the last frame still gets its full eight frames of explosion.
    ///
    /// Reaching zero also clears the pending sound requests (<c>0xEE83</c>), which
    /// matters: without it the blast that killed the player is still queued when the
    /// cave reloads and speaks over the first frame of the new one.
    /// </summary>
    private void CountDownDeath()
    {
        DeathComplete = false;
        if (_playerDeathTimer == 0) return;

        _playerDeathTimer--;
        if (_playerDeathTimer != 0) return;

        _soundFlags = 0;
        DeathComplete = true;
    }

    /// <summary>What every caller of the blast subtracts to turn the cell it cares
    /// about into the block's upper-left corner. The centre is always
    /// <c>anchor + 0x102</c>.</summary>
    public const int BlastCentreOffset = 0x102;

    /// <summary>The player's cell address, or -1 if the cave has no player.</summary>
    public int PlayerCell
    {
        get => _playerCell;
        set => _playerCell = value;
    }

    /// <summary>One pass of the cell scan, matching <c>0x140DE</c>.</summary>
    public void Step()
    {
        // The six interrupts the main loop waits for before it runs, from the pacing
        // wait at 0xEDA5 into 0x1334F - it spins until the interrupt has fired six
        // times, then resets that counter and gets on with the pass.
        //
        // So the interrupts are modelled here, at the top, ahead of the player, the
        // scan and the clock. Nothing brackets those with cli, so the hardware could
        // genuinely have interrupted mid-scan and no placement is exactly right;
        // this is the one that matches where the original actually idles.
        _soundFrames.Clear();
        for (var i = 0; i < TicksPerStep; i++) Interrupt();

        // 0xEDBC and 0xEDBF, both unconditional and both ahead of the scan at
        // 0xEDC2. Only the player's own routine two lines above them is gated on
        // the death timer, so the worms keep crawling through an explosion.
        MoveWorm(_wormA);
        MoveWorm(_wormB);

        BeginScan();

        for (var y = 0; y < CaveGrid.Height; y++)
        {
            var row = y * CaveGrid.RowStride;
            for (var x = 0; x < CaveGrid.Width; x++)
            {
                var addr = row + x * CaveGrid.CellStride;
                var value = _grid.ValueAt(addr);

                // 0x140F6: cmp al,0x4 / jae dispatch. Anything below 4 is inert.
                if (value >= 4)
                    Dispatch(addr, value);
            }
        }

        EndScan();

        // The main loop's tail, in the order it runs it: the scan is called at
        // 0xEDC2, the clock at 0xEE73 and the death countdown at 0xEE76.
        //
        // This order is why a death lasts seven steps and not eight. Whatever
        // starts it - the scan, or the clock running out - writes 8, and the
        // countdown at 0xEE76 runs in that same iteration and takes it to 7. The
        // port used to count down before the scan, which gave every death one frame
        // too many.
        TickClock();
        CountDownDeath();

        // 0xEDD5 increments the frame counter AFTER the scan, so during scan N it still
        // reads N. The open exit animates on odd passes because of it.
        TickCounter++;
    }

    private void Dispatch(int addr, byte value)
    {
        switch (value)
        {
            case RuntimeTile.Boulder: RestingBoulder(addr); break;
            case RuntimeTile.BoulderFalling: FallingBoulder(addr); break;
            case RuntimeTile.Treasure: RestingTreasure(addr); break;
            case RuntimeTile.TreasureFalling: FallingTreasure(addr); break;

            case >= RuntimeTile.Amoeba and <= RuntimeTile.AmoebaLast:
                Amoeba(addr, value);
                break;

            // 0x11B54 turns clockwise and leaves treasure; 0x11B91 turns the other way
            // and leaves space. Everything else about them is shared.
            case RuntimeTile.EnemyClockwise: Enemy(addr, turn: +1, kind: 1); break;
            case RuntimeTile.EnemyAnticlockwise: Enemy(addr, turn: -1, kind: 2); break;

            // 0x140DA and 0x1446F: the just-moved states, low byte only.
            case RuntimeTile.EnemyClockwiseMoved: _grid[addr] = RuntimeTile.EnemyClockwise; break;
            case RuntimeTile.EnemyAnticlockwiseMoved: _grid[addr] = RuntimeTile.EnemyAnticlockwise; break;

            // The two taps and what they shoot. 0x14445 and 0x140AE.
            case >= RuntimeTile.AcidTap and <= RuntimeTile.AcidTapLast: AcidTap(addr, value); break;
            case >= RuntimeTile.FireTap and <= RuntimeTile.FireTapLast: FireTap(addr, value); break;

            case 0x23: AcidTerminal23(addr); break;      // 0x14322
            case 0x24: AcidTerminal24(addr); break;      // 0x146A0
            case 0x25: AcidTerminal25(addr); break;      // 0x146B8
            case 0x2B: AcidTerminal2B(addr); break;      // 0x1434E
            case 0x2F: FireTerminal2F(addr); break;      // 0x14473

            // The deferred seeds. Each writes only its value byte, as the original does.
            case RuntimeTile.SeedAcidSettled: _grid[addr] = RuntimeTile.AcidSettled; break;   // 0x1443D
            case RuntimeTile.SeedFireHead: _grid[addr] = RuntimeTile.FireHead; break;         // 0x145AF
            case RuntimeTile.SeedAcidBlocked2:                                                // 0x146D0
                _grid.WriteWord(addr, 0xFF00 | RuntimeTile.AcidBlocked2);
                break;
            // 0x13ED4 and 0x1460F. Value byte only, like every other seed.
            case RuntimeTile.SeedA7: _grid[addr] = RuntimeTile.WormALast; break;
            case RuntimeTile.SeedB7: _grid[addr] = RuntimeTile.WormBLast; break;

            case >= RuntimeTile.TimedWall and <= RuntimeTile.TimedWallLast:      // 0x1467F
                TimedWall(addr);
                break;

            // The two dormant entities, counting down to a worm. 0x13ED8 and 0x14613.
            case >= RuntimeTile.TimedEntityA and <= RuntimeTile.TimedEntityALast:
                TimedEntity(addr, value, TimedEntityATimer,
                            RuntimeTile.TimedEntityA, RuntimeTile.TimedEntityALast,
                            RuntimeTile.WormA);
                break;

            case >= RuntimeTile.TimedEntityB and <= RuntimeTile.TimedEntityBLast:
                TimedEntity(addr, value, TimedEntityBTimer,
                            RuntimeTile.TimedEntityB, RuntimeTile.TimedEntityBLast,
                            RuntimeTile.WormB);
                break;

            // The two worms' bodies. 0x13E78 and 0x145B3 - they age a segment by one
            // a scan, or mirror the whole body when the movement routine reversed it.
            case >= RuntimeTile.WormA and <= RuntimeTile.WormALast:
                WormBody(addr, value, _wormA, RuntimeTile.WormA, RuntimeTile.WormALast);
                break;

            case >= RuntimeTile.WormB and <= RuntimeTile.WormBLast:
                WormBody(addr, value, _wormB, RuntimeTile.WormB, RuntimeTile.WormBLast);
                break;

            case RuntimeTile.ExpandingWall: ExpandingWall(addr); break;
            case RuntimeTile.ExpandingWallDeferred: _grid.WriteWord(addr, 0xFE15); break;

            case RuntimeTile.ExitClosed: ExitClosed(addr); break;
            case >= RuntimeTile.ExitOpen and <= RuntimeTile.ExitOpenLast: ExitOpen(addr); break;

            // 0x140A4 and 0x140A9: a deferred state simply becomes its live form on
            // the next visit, which is the step after the one that created it.
            case RuntimeTile.BoulderFallingDeferred:              // 0x140A4
                _grid.WriteWord(addr, 0xFF00 | RuntimeTile.BoulderFalling);
                break;
            case 0x45:                                           // 0x140A9
                _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Boulder);
                break;
            case RuntimeTile.TreasureFallingDeferred:            // 0x142F7
                _grid.WriteWord(addr, 0xFF00 | RuntimeTile.TreasureFalling);
                break;
            case 0x41:                                           // 0x142FC
                _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Treasure);
                break;

            // The terminal of each phase chain, each its own dispatch entry. They
            // replace the counted-up value with whatever the chain was producing.
            case 0x14: _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Treasure); break;   // 0x14126
            case 0x1A: _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Treasure); break;   // 0x1412B
            case 0x1E: _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Space); break;      // 0x14130
            case 0x37: _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Boulder); break;    // 0x14135
            case 0x3F: _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Boulder); break;    // 0x14140

            // 0x1413A calls 0x1356C, which writes 0x90 facing left with a cleared
            // shadow. The one terminal in this run that produces a creature rather
            // than a boulder or a gem, and the port did not dispatch it at all.
            //
            // 0x90 is the ANTICLOCKWISE family, not the clockwise one - 0x8C is
            // clockwise. This case named the wrong constant when it landed, which
            // mattered: the two families turn opposite ways and explode into
            // different things. It is also the half of a pair, because the second
            // worm's transform at 0x11B49 turns 0x90 back into a boulder.
            case 0x3B:                                                                // 0x1356C
                _grid[addr] = RuntimeTile.EnemyAnticlockwise;      // mov [bx],0x90
                _grid[addr + 1] = FacingTag | DirLeft;             // mov [bx+1],0xf8
                _grid.WriteWord(addr + CaveGrid.ShadowPlane, 0xFFFF);
                break;
            case 0x73: _grid.WriteWord(addr, 0xFF00 | 0x70); break;                   // 0x14145

            // 0x14441 and the two at 0x13ED0 and 0x1460B write only the VALUE byte,
            // leaving the state byte alone. That is the instruction they use, so the
            // port uses it too.
            case 0x6F: _grid[addr] = 0x6C; break;
            case 0x4F: _grid[addr] = 0xA0; break;
            case 0x5F: _grid[addr] = 0xB0; break;

            default:
                if (IsPhaseChain(value)) AdvancePhase(addr, value);
                break;
        }
    }

    /// <summary>
    /// The runs that <c>0x1467C</c> advances with a bare <c>inc WORD PTR [si]</c>,
    /// read off the dispatch table. Everything in one of these simply counts up until
    /// it reaches a terminal value that some other handler replaces.
    /// </summary>
    private static bool IsPhaseChain(byte v) => v
        is 0x11 or 0x12 or 0x13
        or 0x17 or 0x18 or 0x19
        or 0x1B or 0x1C or 0x1D
        or 0x1F or 0x20 or 0x21 or 0x22
        or 0x28 or 0x29 or 0x2A
        or 0x2C or 0x2D or 0x2E
        or 0x34 or 0x35 or 0x36
        or 0x38 or 0x39 or 0x3A
        or 0x3C or 0x3D or 0x3E
        or 0x6C or 0x6D or 0x6E
        or 0x70 or 0x71 or 0x72;

    private void AdvancePhase(int addr, byte value)
    {
        // 0x1467C increments the whole word, so the state byte rides along. Every
        // producer of these values writes 0xFF as the state, so it stays 0xFF.
        _grid.WriteWord(addr, (ushort)(_grid.ReadWord(addr) + 1));

        // The terminal values are handled by their own dispatch entries, which run on
        // the NEXT visit. Nothing else to do here.
        _ = value;
    }

    /// <summary>
    /// The 3 by 3 blast, from <c>0x11327</c>. <paramref name="upperLeft"/> is the
    /// top-left cell of the block, which every caller computes by stepping back from
    /// the cell it cares about.
    ///
    /// Kind 1 leaves treasure and spares steel and the producers. Kind 2 leaves space
    /// and spares the exit as well. The exit therefore survives a kind-2 blast but not
    /// a kind-1 one.
    /// </summary>
    public void Blast(int upperLeft, int kind)
    {
        _soundFlags |= 0x08;    // 0x11334

        // Reported at the CENTRE, not at the corner the handler is given. A caller
        // passes the upper-left of the 3 by 3 block, which is one cell left and one
        // row up from the thing that actually blew up, and a front end wants the
        // bang. BlastCentreOffset is that step.
        Observe(Observation.Blasted(upperLeft + BlastCentreOffset, kind));

        foreach (var offset in BlastOffsets)
        {
            var cell = upperLeft + offset;

            if (kind == 1)
                BlastCellTreasure(cell);
            else
                BlastCellSpace(cell);

            // 0x1137E: this runs for every cell in the block, immune or not.
            if (cell == _playerCell)
                _playerDeathTimer = 8;
        }
    }

    /// <summary>The offsets at file <c>0x22D0</c>, terminated by <c>0xFFFF</c>.</summary>
    private static readonly int[] BlastOffsets =
    [
        0x0000, 0x0002, 0x0004,
        0x0100, 0x0102, 0x0104,
        0x0200, 0x0202, 0x0204,
    ];

    /// <summary>Kind 1, from <c>0x12AAE</c>. Spares steel and the producers only.</summary>
    private void BlastCellTreasure(int cell)
    {
        var w = _grid.ReadWord(cell);
        if (w == 0xFF02) return;                      // steel
        if (w is >= 0xFF08 and <= 0xFF0F) return;     // the two activatable producers

        _grid.WriteWord(cell, 0xFF11);
        _grid.WriteWord(cell + CaveGrid.ShadowPlane, 0xFFFF);
    }

    /// <summary>Kind 2, from <c>0x1134C</c>. Spares steel, the producers, and the exit.</summary>
    private void BlastCellSpace(int cell)
    {
        var w = _grid.ReadWord(cell);
        if (w == 0xFF02) return;                      // steel
        if (w is >= 0xFF08 and <= 0xFF0F) return;     // producers
        if (w == 0xFF07) return;                      // closed exit
        if (w is >= 0xFF30 and <= 0xFF33) return;     // open exit

        _grid.WriteWord(cell, 0xFF1B);
        _grid.WriteWord(cell + CaveGrid.ShadowPlane, 0xFFFF);
    }

    private void BeginScan()
    {
        // 0x140DF and 0x140E5.
        _amoebaCount = 0;
        _amoebaEnclosed = true;
    }

    private void EndScan()
    {
        // 0x14114, in this order. The first test is why a cave with no amoeba does not
        // fire this on its very first scan.
        if (CaveHasAmoeba && _amoebaEnclosed)
            ConvertWholeColony(toTreasureBlast: true);
    }
}
