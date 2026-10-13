namespace Rockford.Reference;

/// <summary>
/// Channel 0 of the 8253, which is the only clock this game has.
/// </summary>
/// <remarks>
/// <b>This file came from the Alley Cat harness and its description came with
/// it.</b> It used to say that the game programs a divisor of 0CFFFh at
/// <c>0x16E9</c> and runs at 22.4 Hz. That is true of the other game and false
/// of this one, which writes <c>4DAEh</c> at file offset <c>0x213D0</c> and
/// runs at <b>60.0 Hz</b> - the rate <c>docs/PORT-PLAN.md</c> derives by
/// reading and this harness watches it write. A carried-over comment that
/// names a specific address and a specific number is worse than no comment,
/// because it reads like evidence.
///
/// What this game actually does with the clock:
///
/// <list type="bullet">
/// <item>Its own INT 8 handler increments a counter that the frame pacer at
/// <c>0x1D87E</c> waits seven of, so the simulation runs at 60/7 Hz.</item>
/// <item>Nothing reads <c>INT 1Ah</c>. The BIOS tick at <c>0040:006Ch</c> is
/// maintained by the BIOS stub for the sake of anything that chains, and the
/// game is not one of them.</item>
/// </list>
///
/// This is a model, not a cycle-exact 8253. It counts down linearly and wraps,
/// which is what every read in this program is actually looking at; it does
/// not implement mode 3's decrement-by-two or the gate input, because nothing
/// here can observe either. What matters is that it is <b>deterministic</b>:
/// the same instruction count always gives the same counter, so two runs of
/// the same script are the same run.
///
/// <b>The instruction timing underneath it is an approximation</b> - three PIT
/// ticks an instruction, and two for each iteration of a repeated string
/// operation past the first. It is exact and reproducible, and it is not the
/// speed of a real 8088. Nothing here establishes that the game runs at its
/// original speed, only that it runs at a fixed one.
/// </remarks>
public sealed class Pit8253
{
    /// <summary>The 8253 input, 1.193182 MHz.</summary>
    public const long Frequency = 1_193_182;

    /// <summary>
    /// PIT ticks per instruction executed.
    /// </summary>
    /// <remarks>
    /// The 8088 in a PC runs at 4.772727 MHz, exactly four times the PIT, and
    /// averages something near twelve cycles an instruction on this kind of
    /// code. Twelve CPU cycles is three PIT ticks, which is where this comes
    /// from. It is an approximation of speed, but it is an <i>exact</i> and
    /// reproducible one, and both sides of a comparison run off it.
    /// </remarks>
    public const int TicksPerInstruction = 3;

    /// <summary>Divisor for channel 0. Zero means 65,536, as the hardware does.</summary>
    public int Divisor { get; private set; } = 0x10000;

    /// <summary>How many times channel 0 has wrapped - the IRQ 0 count.</summary>
    public long Wraps { get; private set; }

    private long ticks;
    private long wrapCarry;
    private ushort latched;
    private bool isLatched;
    private bool readHigh;
    private bool writeHigh;
    private int pendingDivisor;

    /// <summary>The counter now, counting down from the divisor.</summary>
    public ushort Counter => (ushort)(Divisor - 1 - ticks % Divisor);

    /// <summary>Advance the clock by one instruction's worth.</summary>
    public void Step()
    {
        ticks += TicksPerInstruction;
        wrapCarry += TicksPerInstruction;

        while (wrapCarry >= Divisor)
        {
            wrapCarry -= Divisor;
            Wraps++;
        }
    }

    /// <summary>
    /// The tick count <c>INT 1Ah</c> reports.
    /// </summary>
    /// <remarks>
    /// The BIOS keeps this at <c>0040:006Ch</c> and bumps it on every IRQ 0,
    /// so it is the wrap count and nothing else. Because the game reprograms
    /// the divisor, this speeds up partway through the boot - which is real
    /// behaviour, not drift.
    /// </remarks>
    public ushort BiosTick => (ushort)Wraps;

    /// <summary>
    /// Which CGA scanline the beam is on, 0 to 261.
    /// </summary>
    /// <remarks>
    /// Not part of the 8253, but it belongs on the same clock. A CGA frame is
    /// 262 lines of which 200 are visible, at 60 Hz - so a line is
    /// <c>1193182 / 60 / 262</c> PIT ticks, which is 75.9, taken as 76.
    /// </remarks>
    public int Scanline() => (int)(ticks / TicksPerScanline % Lines);

    /// <summary>
    /// Whether the vertical retrace bit on port 3DAh is set.
    /// </summary>
    /// <remarks>
    /// <b>Not</b> every line outside the visible 200. The 6845 on a CGA holds
    /// vertical sync for sixteen scanlines, positioned by R7 - about 1.0 ms,
    /// where "everything past line 200" would be 3.9 ms. Getting that wrong
    /// gives the game nearly four times the window to move in, and the world
    /// scroll at <c>0x0500</c> is gated on exactly this bit. Every
    /// instruction and every input could be identical and the cat would still
    /// move differently. Found by Astra.
    ///
    /// The front porch of a 262-line CGA frame is the reason sync starts at
    /// 224 rather than at 200.
    /// </remarks>
    public bool InVerticalRetrace()
    {
        var line = Scanline();
        return line >= SyncStart && line < SyncStart + SyncLines;
    }

    public const int Lines = 262;
    public const int VisibleLines = 200;
    public const int TicksPerScanline = 76;

    /// <summary>The first line of vertical sync, from the 6845's R7.</summary>
    public const int SyncStart = 224;

    /// <summary>How long the 6845 holds vertical sync: sixteen lines.</summary>
    public const int SyncLines = 16;

    /// <summary>PIT ticks elapsed. The single clock everything here derives from.</summary>
    public long Ticks => ticks;

    /// <summary>A write to the mode port, 43h.</summary>
    public void WriteMode(byte value)
    {
        // Only channel 0 exists here, and only two commands are used: a latch
        // (00h) and a full lo/hi mode-3 load (36h).
        if ((value & 0xC0) != 0)
        {
            return; // not channel 0
        }

        if ((value & 0x30) == 0)
        {
            latched = Counter;
            isLatched = true;
            readHigh = false;
            return;
        }

        writeHigh = false;
        pendingDivisor = 0;
    }

    /// <summary>A read of the data port, 40h. Low byte first, then high.</summary>
    public byte Read()
    {
        var value = isLatched ? latched : Counter;

        if (readHigh)
        {
            readHigh = false;
            isLatched = false;
            return (byte)(value >> 8);
        }

        readHigh = true;
        return (byte)value;
    }

    /// <summary>A write to the data port, 40h. Low byte first, then high.</summary>
    public void Write(byte value)
    {
        if (writeHigh)
        {
            pendingDivisor |= value << 8;
            Divisor = pendingDivisor == 0 ? 0x10000 : pendingDivisor;
            writeHigh = false;
            wrapCarry = 0;
            return;
        }

        pendingDivisor = value;
        writeHigh = true;
    }
}
