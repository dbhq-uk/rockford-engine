namespace Rockford.Reference;

/// <summary>
/// A real-mode 8086 sufficient to run this game, and nothing more.
/// </summary>
/// <remarks>
/// This exists to be the answer to "does the port behave like the original".
/// Reading a routine and porting it gives an opinion; running the original's
/// own bytes and diffing the framebuffer gives an answer.
///
/// It is deliberately not a general emulator. It implements the instructions
/// this 26 KB program actually uses, and throws on anything else rather than
/// guessing - an unimplemented opcode that silently does nothing would produce
/// a plausible wrong frame, which is the worst possible outcome for a
/// comparison harness.
/// </remarks>
public sealed class Machine8086
{
    public const int MemorySize = 1 << 20;

    /// <summary>Where an EGA in a graphics mode puts its four planes.</summary>
    public const int VideoSegment = 0xA000;

    public byte[] Memory { get; } = new byte[MemorySize];

    /// <summary>The card, the disks - whatever this particular machine wears.</summary>
    public IPeripherals Peripherals { get; set; } = NoPeripherals.Instance;

    /// <summary>The BIOS and DOS services the program has not hooked itself.</summary>
    public IServices Services { get; set; } = NoServices.Instance;

    /// <summary>The paragraph the program was loaded at, and how long it is.</summary>
    /// <remarks>
    /// This is what separates "the program hooked this interrupt" from "this is
    /// a service call". A booter has one segment and the test is an equality;
    /// a 142 KB EXE spans 0x22C0 paragraphs and it has to be a range, or the
    /// only hooked vector recognised is whichever one happens to live in the
    /// first 64 KB.
    /// </remarks>
    public ushort LoadSegment { get; set; }

    public ushort LoadParagraphs { get; set; }

    /// <summary>True when a vector points into the loaded program.</summary>
    public bool HookedByProgram(byte number)
    {
        if (LoadParagraphs == 0) return false;

        var segment = ReadWord(0, (ushort)(number * 4 + 2));
        return segment >= LoadSegment && segment < LoadSegment + LoadParagraphs;
    }

    // General registers, in the 8086's own order so that r/m decoding is a
    // table lookup rather than a switch.
    public ushort[] Registers { get; } = new ushort[8]; // AX CX DX BX SP BP SI DI
    public ushort[] Segments { get; } = new ushort[4];  // ES CS SS DS

    public ushort Ip { get; set; }
    public ushort Flags { get; set; } = 0xF002;

    /// <summary>Instructions executed. A run is bounded by this, not by time.</summary>
    public long Executed { get; private set; }

    /// <summary>Set when the program halts or jumps to itself.</summary>
    public bool Stopped { get; private set; }

    public const int AX = 0, CX = 1, DX = 2, BX = 3, SP = 4, BP = 5, SI = 6, DI = 7;
    public const int ES = 0, CS = 1, SS = 2, DS = 3;

    private const ushort CarryFlag = 0x0001;
    private const ushort ParityFlag = 0x0004;
    private const ushort AuxFlag = 0x0010;
    private const ushort ZeroFlag = 0x0040;
    private const ushort SignFlag = 0x0080;
    private const ushort TrapFlag = 0x0100;
    private const ushort InterruptFlag = 0x0200;
    private const ushort DirectionFlag = 0x0400;
    private const ushort OverflowFlag = 0x0800;

    /// <summary>Ports the game writes, recorded rather than emulated.</summary>
    public List<(int Port, int Value)> PortWrites { get; } = [];

    /// <summary>Software interrupts taken, so the harness can see BIOS use.</summary>
    public List<(int Number, ushort Ax)> Interrupts { get; } = [];

    /// <summary>
    /// The BIOS tick <c>INT 1Ah</c> reports.
    /// </summary>
    /// <remarks>
    /// Settable so an existing static comparison can pin it, but by default it
    /// comes off <see cref="Pit"/>, which is where a real one comes from - the
    /// BIOS bumps <c>0040:006Ch</c> on every IRQ 0 and nothing else.
    /// </remarks>
    public ushort? BiosTickOverride { get; set; }

    public ushort BiosTick
    {
        get => BiosTickOverride ?? Pit.BiosTick;
        set => BiosTickOverride = value;
    }

    /// <summary>Channel 0 of the 8253 - the only clock this program has.</summary>
    public Pit8253 Pit { get; } = new();

    /// <summary>Keyboard scancode the emulated port 60h returns.</summary>
    public byte Scancode { get; set; }

    /// <summary>Port 61h, which reads back what was last written to it.</summary>
    public byte PortB { get; set; }

    /// <summary>
    /// Every hardware value the program observed, in the order it read them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A differential comparison snapshots memory at a routine's first
    /// instruction, but the routine reads the clock and the beam a few
    /// instructions later - and on 3.6% of calls to <c>entity_tick</c> the
    /// timer ticked in between. The port then answered a pacing gate
    /// differently from the original for a reason that had nothing to do
    /// with the port.
    /// </para>
    /// <para>
    /// A single "last value read" was enough while every routine read the
    /// clock once. <c>0x11C6</c> does not: it busy-waits on <c>INT 1Ah</c>
    /// until ten ticks have passed, calling a sound routine each time round
    /// that writes four words of memory. How many times it goes round is
    /// therefore <i>observable</i>, and a port can only reproduce it by
    /// seeing the same sequence of clock readings.
    /// </para>
    /// <para>
    /// So it is a list, replayed in order. A hardware input is not the thing
    /// under test; how the program responds to it is.
    /// </para>
    /// </remarks>
    public List<ushort> TickReads { get; } = [];

    public List<bool> RetraceReads { get; } = [];

    public List<byte> TimerReads { get; } = [];

    /// <summary>Forget what was read, at the start of a comparison window.</summary>
    public void ForgetHardwareReads()
    {
        TickReads.Clear();
        RetraceReads.Clear();
        TimerReads.Clear();
    }

    /// <summary>
    /// Set for exactly one instruction after <c>sti</c>.
    /// </summary>
    /// <remarks>
    /// The 8086 does not recognise an interrupt until the instruction
    /// following <c>sti</c> has run, which is what makes <c>sti / ret</c> a
    /// safe way out of a handler. Without modelling it, a delivery can land
    /// inside a critical section that the program closed on the very next
    /// instruction. Found by Astra.
    /// </remarks>
    public bool InterruptShadow { get; set; }

    /// <summary>Whether the beam is in vertical retrace, recorded as it is read.</summary>
    /// <remarks>
    /// Lives here rather than on the card because it comes off the same clock
    /// as everything else, and because the sequence of answers is observable:
    /// a program that waits for retrace goes round its loop a number of times
    /// that depends on them, so a port can only match by seeing the same list.
    /// </remarks>
    public bool ReadRetrace()
    {
        var inRetrace = Pit.InVerticalRetrace();
        RetraceReads.Add(inRetrace);
        return inRetrace;
    }

    /// <summary>
    /// Set between raising an interrupt and the handler's EOI to port 20h.
    /// </summary>
    /// <remarks>
    /// Real hardware will not deliver the next IRQ until the PIC is told the
    /// last one is done, and this program's ISR ends with
    /// <c>mov al, 20h / out 20h, al</c> at <c>0x15C0</c>. Without modelling
    /// that, a fast script re-enters the ISR on top of itself and the key
    /// state bytes at <c>06BAh</c> end up describing a keyboard nobody typed on.
    /// </remarks>
    public bool InterruptInService { get; set; }

    /// <summary>
    /// What <c>INT 11h</c> reports. Bits 4-5 are the display.
    /// </summary>
    /// <remarks>
    /// <c>0020h</c> is 80x25 colour, which is what this game wants and
    /// what every script has had. <c>0030h</c> is 80x25 monochrome,
    /// and <c>0x5F50</c> takes a completely different path on it -
    /// writing <c>55AAh</c> into <c>B800:0000</c> and reading it back
    /// to see whether a colour card is there anyway. That probe and
    /// the two messages behind it are unreachable on a colour machine
    /// by definition, not by any fault of the script.
    /// </remarks>
    public ushort Equipment { get; set; } = 0x0020;

    /// <summary>
    /// A data-segment address to report every write to, with the address
    /// of the instruction that did it.
    /// </summary>
    /// <remarks>
    /// The last resort, and the one that settles an argument. When a byte
    /// holds a value nothing in the disassembly seems able to have put
    /// there, reading harder is guessing; this says which instruction wrote
    /// it.
    /// </remarks>
    public int Watch { get; set; } = -1;

    public List<(ushort At, byte Value)> Writes { get; } = [];

    /// <summary>Hardware interrupts delivered, by vector.</summary>
    public Dictionary<int, long> RaisedCounts { get; } = [];

    /// <summary>
    /// Deliver a hardware interrupt the way the 8086 does.
    /// </summary>
    /// <remarks>
    /// Push flags, CS and IP, clear IF and TF, then vector through the real
    /// interrupt table in low memory. That last part is the whole point: the
    /// game installs its own handler at <c>0000:0024h</c> from
    /// <c>hook_keyboard</c> at <c>0x14E4</c>, so vectoring properly runs the
    /// <i>game's</i> code - including the <c>repne scasb</c> over its key
    /// table, the state bytes, and the counter at <c>[693h]</c> that drives
    /// every frame of work. Emulating a keyboard instead of raising the
    /// interrupt would have meant reimplementing the thing under test.
    /// </remarks>
    /// <summary>
    /// Vector through the IVT without claiming the interrupt
    /// controller.
    /// </summary>
    /// <remarks>
    /// <b>A PCjr's keyboard arrives already acknowledged.</b> The BIOS
    /// takes IRQ 1, writes its own EOI and then calls <c>INT 48h</c>,
    /// so the game's handler at <c>0x15C6</c> never writes port 20h -
    /// and a harness that marks the interrupt in service on the way in
    /// has nothing to clear it. The first key is delivered, the flag
    /// stays set, and no key is ever delivered again. That is exactly
    /// how far the first PCjr run got: the menu, and nothing past it.
    /// </remarks>
    public void RaiseAcknowledged(byte vector)
    {
        Raise(vector);
        InterruptInService = false;
        InService = 0;
    }

    /// <summary>
    /// Deliver a hardware line, marking it in service.
    /// </summary>
    /// <remarks>
    /// The IRQ, not the vector, because priority is a property of the line:
    /// IRQ 0 is vector 8 and IRQ 1 is vector 9, and which of them may
    /// interrupt the other is decided by the 0 and the 1.
    /// </remarks>
    public void RaiseLine(int irq)
    {
        InService |= 1 << irq;
        Raise((byte)(0x08 + irq));
    }

    public void Raise(byte vector)
    {
        Push(Flags);
        Push(Segments[CS]);
        Push(Ip);

        InterruptsEnabled = false;
        SetFlag(TrapFlag, false);

        Ip = ReadWord(0, (ushort)(vector * 4));
        Segments[CS] = ReadWord(0, (ushort)(vector * 4 + 2));

        InterruptInService = true;
        RaisedCounts[vector] = RaisedCounts.GetValueOrDefault(vector) + 1;
    }

    public static int Linear(ushort segment, ushort offset) => (segment << 4) + offset & 0xFFFFF;

    /// <summary>
    /// The card holding the aperture at A000, if one is fitted.
    /// </summary>
    /// <remarks>
    /// Video memory is not memory on this machine, and that is the largest
    /// single difference from the CGA harness this interpreter came from. Every
    /// access inside the aperture goes through the card, because what the
    /// program wrote and what the planes now hold are related by registers the
    /// instruction never mentions - and because a read there has a side effect,
    /// loading the latches that the next write will place.
    /// </remarks>
    public Display? Video { get; set; }

    public byte ReadByte(ushort segment, ushort offset)
    {
        var at = Linear(segment, offset);

        return Video is not null && Display.Owns(at)
            ? Video.ReadAperture(at - Display.ApertureBase)
            : Memory[at];
    }

    public ushort ReadWord(ushort segment, ushort offset) =>
        (ushort)(ReadByte(segment, offset) | ReadByte(segment, (ushort)(offset + 1)) << 8);

    public void WriteByte(ushort segment, ushort offset, byte value)
    {
        var at = Linear(segment, offset);

        if (at == Watch && Writes.Count < 200)
        {
            Writes.Add((WatchFrom, value));
        }

        if (Video is not null && Display.Owns(at))
        {
            Video.WriteAperture(at - Display.ApertureBase, value);
            return;
        }

        Memory[at] = value;
    }

    /// <summary>The instruction currently executing, for the watchpoint.</summary>
    public ushort WatchFrom { get; set; }

    public void WriteWord(ushort segment, ushort offset, ushort value)
    {
        WriteByte(segment, offset, (byte)value);
        WriteByte(segment, (ushort)(offset + 1), (byte)(value >> 8));
    }

    /// <summary>
    /// The raw window at A000. NOT what a comparison looks at.
    /// </summary>
    /// <remarks>
    /// On a CGA the framebuffer is the memory, so copying it out was the whole
    /// job. An EGA in mode 0Dh has four 8,000-byte planes behind one 64 KB
    /// aperture, and what the program wrote last is not what any one of them
    /// holds. Ask <see cref="Display"/> for the planes; this is here for the
    /// handful of places that want the aperture itself.
    /// </remarks>
    public byte[] VideoAperture(int length = 0x10000)
    {
        var video = new byte[length];
        Array.Copy(Memory, VideoSegment << 4, video, 0, video.Length);
        return video;
    }

    public bool Flag(ushort mask) => (Flags & mask) != 0;

    public void SetFlag(ushort mask, bool value) =>
        Flags = (ushort)(value ? Flags | mask : Flags & ~mask);

    public bool Carry { get => Flag(CarryFlag); set => SetFlag(CarryFlag, value); }
    public bool Zero { get => Flag(ZeroFlag); set => SetFlag(ZeroFlag, value); }
    public bool Sign { get => Flag(SignFlag); set => SetFlag(SignFlag, value); }
    public bool Overflow { get => Flag(OverflowFlag); set => SetFlag(OverflowFlag, value); }
    public bool Direction { get => Flag(DirectionFlag); set => SetFlag(DirectionFlag, value); }
    public bool InterruptsEnabled { get => Flag(InterruptFlag); set => SetFlag(InterruptFlag, value); }
    public bool Auxiliary { get => Flag(AuxFlag); set => SetFlag(AuxFlag, value); }
    public bool Parity { get => Flag(ParityFlag); set => SetFlag(ParityFlag, value); }

    public void Halt() => Stopped = true;

    /// <summary>Where the BIOS keeps its tick count: <c>0040:006Ch</c>.</summary>
    /// <remarks>
    /// <para>
    /// <b>Maintained by the BIOS timer stub, not by this class.</b> The first
    /// version assigned the 8253's wrap count here on every instruction, which
    /// is wrong in three ways that a differential reference cannot afford: it
    /// advanced while interrupts were disabled, it advanced during the
    /// instruction charging of a <c>rep</c> without the guest running anything,
    /// and it overwrote the guest's own writes - a program that set the tick to
    /// 100 found it back at 2 on the next wrap.
    /// </para>
    /// <para>
    /// It also counted ticks the real BIOS would never have seen. Rockford
    /// hooks INT 8 and chains only sometimes: a VGA title run recorded 70 wraps
    /// against 19 visits to the BIOS handler, and only those 19 bump a real
    /// 0040:006Ch.
    /// </para>
    /// </remarks>
    public const int BiosTickAddress = 0x46C;

    internal void CountInstruction()
    {
        Executed++;
        Pit.Step();
    }

    /// <summary>The 8259's interrupt mask, as last written to port 21h.</summary>
    /// <remarks>
    /// A set bit is a masked line. Ignoring this meant IRQ 0 was still
    /// delivered after the program had written FFh to port 21h to turn every
    /// line off, which is a thing a program does precisely because it is about
    /// to do something an interrupt would ruin.
    /// </remarks>
    public byte InterruptMask { get; set; }

    /// <summary>True when the controller would deliver this line.</summary>
    public bool LineEnabled(int irq) => (InterruptMask >> irq & 1) == 0;

    /// <summary>Which lines are in service, lowest number highest priority.</summary>
    /// <remarks>
    /// A single flag for all of them was wrong in one direction that matters:
    /// it blocked IRQ 0 while a keyboard handler was running, when an 8259
    /// lets a higher-priority line nest over a lower one. The keyboard handler
    /// is the long one, and the clock is the thing that must not stop.
    /// </remarks>
    public int InService { get; set; }

    /// <summary>True when the controller would let this line through now.</summary>
    public bool CanNest(int irq)
    {
        // Every line at or below this one in number - which is at or above it
        // in priority - blocks it.
        for (var higher = 0; higher <= irq; higher++)
        {
            if ((InService >> higher & 1) != 0) return false;
        }

        return true;
    }

    /// <summary>
    /// Delivery bookkeeping that must outlive one call to the run loop.
    /// </summary>
    /// <remarks>
    /// These lived as locals in <c>Harness.Run</c>, so stopping and resuming a
    /// run changed what was delivered: two one-instruction runs lost an
    /// interrupt that one two-instruction run delivered. A harness whose
    /// answer depends on where you paused it is not a reference.
    /// </remarks>
    /// <summary>
    /// The wrap the timer was last delivered on.
    /// </summary>
    /// <remarks>
    /// Starts at zero, not -1. At -1 a fresh machine with interrupts enabled
    /// delivered IRQ 0 immediately, on zero PIT wraps - an interrupt for a
    /// tick that had not happened.
    /// </remarks>
    public long LastTimerWrap { get; set; }

    public long LastKeyWrap { get; set; } = long.MinValue;

    /// <summary>How many times each routine has been called.</summary>
    public Dictionary<ushort, long> CallCounts { get; } = [];

    internal void RecordCall(ushort target) =>
        CallCounts[target] = CallCounts.GetValueOrDefault(target) + 1;

    /// <summary>Push a word, as the 8086 does - SP down first, then store.</summary>
    public void Push(ushort value)
    {
        Registers[SP] -= 2;
        WriteWord(Segments[SS], Registers[SP], value);
    }

    public ushort Pop()
    {
        var value = ReadWord(Segments[SS], Registers[SP]);
        Registers[SP] += 2;
        return value;
    }

    /// <summary>Set the flags an arithmetic result implies, at the given width.</summary>
    public void SetResultFlags(int result, bool wide)
    {
        var mask = wide ? 0xFFFF : 0xFF;
        var top = wide ? 0x8000 : 0x80;

        Zero = (result & mask) == 0;
        Sign = (result & top) != 0;

        var low = result & 0xFF;
        var bits = 0;
        for (var i = 0; i < 8; i++)
        {
            bits += low >> i & 1;
        }

        Parity = (bits & 1) == 0;
    }

    /// <summary>
    /// The flags <c>inc</c> and <c>dec</c> set, which are not the ones
    /// <c>add</c> and <c>sub</c> set.
    /// </summary>
    /// <remarks>
    /// They leave carry alone - that is why they exist alongside add and sub -
    /// but they do set overflow and auxiliary carry, and this originally set
    /// neither. Incrementing <c>7FFFh</c> to <c>8000h</c> must set overflow;
    /// leaving a stale one there sends the next signed conditional branch the
    /// wrong way, and nothing about the resulting frame looks like a flag bug.
    /// Found by Astra.
    /// </remarks>
    public void SetIncDecFlags(int before, int result, bool wide, bool decrement)
    {
        var carry = Carry;
        SetResultFlags(result, wide);
        Carry = carry;

        var top = wide ? 0x8000 : 0x80;
        var mask = wide ? 0xFFFF : 0xFF;

        // inc overflows only going from the largest positive to the smallest
        // negative; dec only the other way.
        Overflow = decrement
            ? (before & mask) == top
            : (before & mask) == top - 1;

        // The nibble carry, which BCD arithmetic reads. This program uses aaa
        // for its score, so it is not academic.
        Auxiliary = decrement ? (before & 0xF) == 0 : (before & 0xF) == 0xF;
    }

    /// <summary>
    /// How many of each recording to keep. A program that polls INT 1Ah in a
    /// wait loop takes it millions of times, and an unbounded list turns a
    /// diagnostic into an out-of-memory - which is what happened first.
    /// </summary>
    public const int RecordLimit = 4096;

    /// <summary>Counts by interrupt number, which survive the record limit.</summary>
    public Dictionary<int, long> InterruptCounts { get; } = [];

    internal void RecordInterrupt(int number, ushort ax)
    {
        InterruptCounts[number] = InterruptCounts.GetValueOrDefault(number) + 1;
        if (Interrupts.Count < RecordLimit)
        {
            Interrupts.Add((number, ax));
        }
    }

    internal void RecordPort(int port, int value)
    {
        if (PortWrites.Count < RecordLimit)
        {
            PortWrites.Add((port, value));
        }
    }
}
