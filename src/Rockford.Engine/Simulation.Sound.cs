namespace Rockford.Engine;

public sealed partial class Simulation
{
    /// <summary>The 8253 input clock. A tone is this divided by the divisor.</summary>
    public const int PitClock = 1193180;

    /// <summary>
    /// Sound frames produced during the last <see cref="Step"/>, oldest first.
    ///
    /// There are up to <see cref="TicksPerStep"/> of them because the driver runs
    /// on the interrupt at 60 Hz while a step is one pass of the 10 Hz main loop.
    /// A front end that played only one of these would be throwing five sixths of
    /// the game's sound away.
    /// </summary>
    private readonly List<SoundFrame> _soundFrames = new(TicksPerStep);

    /// <summary>
    /// Which of the footstep's two pitches comes next. <c>ds:0x167c</c>, which
    /// nothing else in the binary touches and which starts as zero in the image -
    /// so the first footstep served is the lower of the two.
    ///
    /// Like <see cref="LastSoundedCell"/> it outlives the cave: the original never
    /// resets it between caves or deaths, so a cave that ended on an odd number of
    /// footsteps hands the next one the other pitch.
    /// </summary>
    public bool FootstepToggle { get; set; }

    /// <summary>
    /// Carries over the state that is a global in the original and so outlives the
    /// cave, from the Simulation being replaced.
    ///
    /// A Simulation is one cave. These are not: nothing in the binary resets any of
    /// them - not the loader, not the respawn, not a new game after a game over - so
    /// a host that rebuilds the cave has to hand them on or it is starting each cave
    /// in a state the original never reaches.
    ///
    /// Note what is NOT here. The timed wall's LATCH, <c>ds:0x1696</c>, is cleared
    /// by the loader at <c>0x108A4</c> - so every cave starts with the wall
    /// inactive, and it stays inactive until something falls into one again. Its
    /// timestamp <c>ds:0x1CF4</c> is not cleared and does carry, but nothing can
    /// read it before the next activation overwrites it.
    /// </summary>
    public void CarryGlobalsFrom(Simulation previous)
    {
        // The footstep: ds:0x1DF8 and ds:0x167C.
        LastSoundedCell = previous.LastSoundedCell;
        FootstepToggle = previous.FootstepToggle;

        // The timed wall's timestamp, ds:0x1CF4. Not its latch: 0x108A4 clears
        // that on every cave load, death reload included.
        TimedWallStart = previous.TimedWallStart;

        // Both worms' headings, ds:0x1610 and ds:0x1618. The loader clears their
        // lengths at 0xFEBA and their head and tail pointers at 0x10878, and
        // deliberately leaves these two alone - so a worm hatches facing whichever
        // way the last worm was facing when its cave ended, possibly several caves
        // ago. Only the low two bits are ever read.
        //
        // Their four remaining flags, ds:0x1698 to ds:0x169E, carry too and are not
        // here. They cannot be read stale: the movement routine clears the pair it
        // owns at the top of every step, and the only handlers that read them are
        // the worm's own body cells, which exist only once its length is non-zero
        // and the routine therefore runs.
        WormADirection = previous.WormADirection;
        WormBDirection = previous.WormBDirection;
    }

    /// <summary>
    /// One 60 Hz interrupt, from <c>0x13368</c>.
    ///
    /// The handler increments its counters and then falls straight through into the
    /// sound driver at <c>0x133A5</c> - there is no call, the driver simply is the
    /// tail of the interrupt. So sound is serviced sixty times a second, not once
    /// per simulation step, and two of its handlers draw from the shared generator
    /// while doing it.
    ///
    /// The counters advance one at a time rather than six at once, so each driver
    /// pass sees the counter value the interrupt would have seen. The two holding
    /// tones gate on the low three bits of that counter, so advancing in one jump
    /// would let at most one of six ticks pass the gate instead of the right
    /// number.
    /// </summary>
    private void Interrupt()
    {
        // Four counters, not two. 0x1337C to 0x1338B increments all of them on
        // every interrupt, and the cave-start routine at 0xFF6E zeroes all four.
        // The two worm timers were simply missing, which is why nothing ever
        // hatched: they are what 0x13EDE and 0x14619 compare against.
        _interruptTicks++;      // ds:0x16A8, 0x1337C
        TimedEntityATimer++;    // ds:0x16AA, 0x13380
        TimedEntityBTimer++;    // ds:0x16AC, 0x13384
        _secondTicks++;         // ds:0x226C, 0x13388

        _soundFrames.Add(ServiceSound());
    }

    /// <summary>
    /// What the driver at <c>0x133A5</c> decided to play on this interrupt, as a PIT
    /// channel 2 divisor. Zero means silence.
    ///
    /// The priority chain is the order of the tests from <c>0x133B5</c> onward and
    /// the first one that actually SPEAKS wins. That distinction is the whole of it:
    /// the two holding tones only speak on every eighth tick, and on the ticks in
    /// between they do not return silence - <c>0x133C7</c> and <c>0x133F4</c> jump
    /// to the next test in the chain. A pending alarm therefore does not gag the
    /// treasure and boulder sounds underneath it; it merely fails to speak itself.
    ///
    /// This lives in the engine rather than a front end for one reason: two of the
    /// handlers pick between two divisors with a draw from the game's own generator.
    /// Making that draw somewhere else, or a different number of times, would leave
    /// the random stream in a different place and every later random event would
    /// drift.
    /// </summary>
    private SoundFrame ServiceSound()
    {
        // 0x80 and 0x40 hold until whatever raised them stops - neither clears its
        // own bit. They speak only when the tick counter's low three bits are zero.
        if ((_soundFlags & 0x80) != 0 && (_secondTicks & 7) == 0)      // 0x133BF
            return new(SoundEvent.Tick, 0x3E8);

        if ((_soundFlags & 0x40) != 0 && (_secondTicks & 7) == 0)      // 0x133EC
            return new(SoundEvent.VariableTone, VariableDivisor);

        if ((_soundFlags & 0x01) != 0)                                 // 0x1340F
        {
            // Treasure landing or rolling. Two pitches, chosen by the low bit of a
            // draw at 0x1341F.
            var d = (_random.Next() & 1) != 0 ? 0x80 : 0xC0;
            _soundFlags &= 0xFE;
            return new(SoundEvent.TreasureFalling, d);
        }

        if ((_soundFlags & 0x02) != 0)                                 // 0x13443
        {
            // The footstep, and the one two-pitch handler that does NOT draw from
            // the generator. 0x13453 tests a toggle byte and flips it:
            //
            //   13453: cmp  BYTE PTR ds:0x167c,0x0
            //   13458: je   0x1346a          ; zero  -> 0x5800, set the byte
            //   1345a: mov  ax,0x5000        ; other -> 0x5000, clear it
            //
            // Its neighbours at 0x1341F and 0x134E4 really do call 0x124B1 and
            // test bit zero, which is why this one reads as though it should.
            // Drawing here would take a number out of the shared stream and every
            // later random event in the cave would land somewhere else - and this
            // fires on every step the player takes.
            var d = FootstepToggle ? 0x5000 : 0x5800;
            FootstepToggle = !FootstepToggle;
            _soundFlags &= 0xFD;
            return new(SoundEvent.LowRumble, d);
        }

        if ((_soundFlags & 0x04) != 0)
        {
            _soundFlags &= 0xFB;
            return new(SoundEvent.Collect, 0x200);
        }

        if ((_soundFlags & 0x08) != 0)
        {
            // Low enough to be a growl.
            _soundFlags &= 0xF7;
            return new(SoundEvent.Blast, 0x4000);
        }

        if ((_soundFlags & 0x10) != 0)
        {
            // A boulder landing or rolling, again two pitches from a draw at 0x134E4.
            var d = (_random.Next() & 1) != 0 ? 0x4400 : 0x4800;
            _soundFlags &= 0xEF;
            return new(SoundEvent.BoulderFalling, d);
        }

        if ((_soundFlags & 0x20) != 0)
        {
            _soundFlags &= 0xDF;
            return new(SoundEvent.WallGrowing, 0x7000);
        }

        // Nothing spoke. A holding tone that is still raised is reported anyway,
        // with a divisor of zero.
        //
        // The hardware cannot tell the difference - zero is silence either way,
        // and this is the silence the original's own fall-through produces. It
        // exists for the enhanced presentation, whose holding tone is a single
        // oscillator started once and left running: it needs "held, not
        // speaking" and "finished" to be different frames, or it has no moment
        // at which to stop.
        if ((_soundFlags & 0x80) != 0) return new(SoundEvent.Tick, 0);
        if ((_soundFlags & 0x40) != 0) return new(SoundEvent.VariableTone, 0);

        return new(SoundEvent.None, 0);
    }

    /// <summary>
    /// The frames the last step produced, oldest first, and clears them.
    ///
    /// Both presentations read this one list. Calling the driver again for the other
    /// would service the requests twice and draw from the generator twice.
    /// </summary>
    public IReadOnlyList<SoundFrame> TakeSoundFrames()
    {
        var frames = _soundFrames.ToArray();
        _soundFrames.Clear();
        return frames;
    }

    /// <summary>The divisor the <c>0x40</c> request uses, held in <c>ds:0x16A6</c>.</summary>
    public int VariableDivisor { get; set; } = 0x1000;

    /// <summary>The pending sound requests, <c>ds:0x2262</c>. Exposed for tests.</summary>
    public byte SoundFlags => _soundFlags;

    /// <summary>
    /// Raises a request, the way the rule handlers do with <c>or</c> against
    /// <c>ds:0x2262</c>. Requests accumulate and the driver serves one an interrupt,
    /// so raising the same sound twice before the next pass is the same as raising
    /// it once - which is why a cave collapsing does not produce a wall of noise.
    /// </summary>
    public void Request(SoundEvent sound) => _soundFlags |= (byte)sound;

    /// <summary>Turns a divisor into a frequency, the way the hardware does.</summary>
    public static double FrequencyOf(int divisor) => divisor == 0 ? 0 : (double)PitClock / divisor;
}
