namespace Rockford.Reference;

/// <summary>
/// Executes the original's own instructions.
/// </summary>
/// <remarks>
/// Covers the subset this game uses. Anything outside it throws
/// <see cref="NotSupportedException"/> naming the opcode and address, because
/// a harness that silently skips an instruction produces a frame that looks
/// almost right, and almost right is worse than a crash when the whole point
/// is a pixel comparison.
/// </remarks>
public sealed class Interpreter(Machine8086 machine)
{
    private readonly Machine8086 m = machine;
    private int segmentOverride = -1;

    /// <summary>Run until stopped or until the budget is spent.</summary>
    public void Run(long maxInstructions)
    {
        for (var i = 0; i < maxInstructions && !m.Stopped; i++)
        {
            Step();
        }
    }

    public void Step()
    {
        if (m.Watch >= 0)
        {
            m.WatchFrom = m.Ip;
        }

        segmentOverride = -1;
        m.InterruptShadow = false;
        var repeat = 0;

        var opcode = Fetch();

        // Prefixes.
        while (true)
        {
            switch (opcode)
            {
                case 0x26: segmentOverride = Machine8086.ES; opcode = Fetch(); continue;
                case 0x2E: segmentOverride = Machine8086.CS; opcode = Fetch(); continue;
                case 0x36: segmentOverride = Machine8086.SS; opcode = Fetch(); continue;
                case 0x3E: segmentOverride = Machine8086.DS; opcode = Fetch(); continue;
                case 0xF2: repeat = 2; opcode = Fetch(); continue;
                case 0xF3: repeat = 3; opcode = Fetch(); continue;
            }

            break;
        }

        m.CountInstruction();
        Execute(opcode, repeat);
    }

    private byte Fetch()
    {
        var value = m.ReadByte(m.Segments[Machine8086.CS], m.Ip);
        m.Ip++;
        return value;
    }

    private ushort FetchWord()
    {
        var low = Fetch();
        return (ushort)(low | Fetch() << 8);
    }

    private ushort DataSegment =>
        m.Segments[segmentOverride >= 0 ? segmentOverride : Machine8086.DS];

    private ushort StackSegment =>
        m.Segments[segmentOverride >= 0 ? segmentOverride : Machine8086.SS];

    /// <summary>A decoded mod/rm: either a register or a segment plus offset.</summary>
    private readonly record struct Operand(int Register, ushort Segment, ushort Offset, bool IsRegister);

    private Operand DecodeModRm(byte modrm, out int reg)
    {
        var mod = modrm >> 6;
        reg = modrm >> 3 & 7;
        var rm = modrm & 7;

        if (mod == 3)
        {
            return new Operand(rm, 0, 0, true);
        }

        ushort offset;
        var segment = DataSegment;

        // BP-relative forms default to SS, which is the one addressing rule
        // that is easy to get wrong and hard to notice.
        switch (rm)
        {
            case 0: offset = (ushort)(m.Registers[Machine8086.BX] + m.Registers[Machine8086.SI]); break;
            case 1: offset = (ushort)(m.Registers[Machine8086.BX] + m.Registers[Machine8086.DI]); break;
            case 2:
                offset = (ushort)(m.Registers[Machine8086.BP] + m.Registers[Machine8086.SI]);
                if (segmentOverride < 0) segment = m.Segments[Machine8086.SS];
                break;
            case 3:
                offset = (ushort)(m.Registers[Machine8086.BP] + m.Registers[Machine8086.DI]);
                if (segmentOverride < 0) segment = m.Segments[Machine8086.SS];
                break;
            case 4: offset = m.Registers[Machine8086.SI]; break;
            case 5: offset = m.Registers[Machine8086.DI]; break;
            case 6:
                if (mod == 0)
                {
                    offset = FetchWord();
                }
                else
                {
                    offset = m.Registers[Machine8086.BP];
                    if (segmentOverride < 0) segment = m.Segments[Machine8086.SS];
                }

                break;
            default: offset = m.Registers[Machine8086.BX]; break;
        }

        if (mod == 1)
        {
            offset = (ushort)(offset + (sbyte)Fetch());
        }
        else if (mod == 2)
        {
            offset = (ushort)(offset + FetchWord());
        }

        return new Operand(0, segment, offset, false);
    }

    private ushort Read(Operand operand, bool wide)
    {
        if (operand.IsRegister)
        {
            return wide ? m.Registers[operand.Register] : ReadByteRegister(operand.Register);
        }

        return wide
            ? m.ReadWord(operand.Segment, operand.Offset)
            : m.ReadByte(operand.Segment, operand.Offset);
    }

    private void Write(Operand operand, bool wide, ushort value)
    {
        if (operand.IsRegister)
        {
            if (wide)
            {
                m.Registers[operand.Register] = value;
            }
            else
            {
                WriteByteRegister(operand.Register, (byte)value);
            }

            return;
        }

        if (wide)
        {
            m.WriteWord(operand.Segment, operand.Offset, value);
        }
        else
        {
            m.WriteByte(operand.Segment, operand.Offset, (byte)value);
        }
    }

    // Byte registers are AL CL DL BL AH CH DH BH - the low four then the high
    // four of the same words, not four separate registers.
    private byte ReadByteRegister(int index) =>
        index < 4 ? (byte)m.Registers[index] : (byte)(m.Registers[index - 4] >> 8);

    private void WriteByteRegister(int index, byte value)
    {
        if (index < 4)
        {
            m.Registers[index] = (ushort)(m.Registers[index] & 0xFF00 | value);
        }
        else
        {
            m.Registers[index - 4] = (ushort)(m.Registers[index - 4] & 0x00FF | value << 8);
        }
    }

    private void Execute(byte opcode, int repeat)
    {
        switch (opcode)
        {
            case 0x90: return; // nop
            case 0xF4: m.Halt(); return;
            // These were no-ops while nothing raised an interrupt. They are
            // not optional now: the game turns interrupts off around the
            // interrupt table write at 0x151B and around every timer read,
            // and delivering INT 9 inside one of those would corrupt exactly
            // the state the critical section exists to protect.
            case 0xFA: m.InterruptsEnabled = false; return;
            // sti also opens a one-instruction shadow: the 8086 will not
            // recognise an interrupt until the instruction after it has run,
            // so that `sti / ret` can leave a handler before anything else
            // arrives. Without it, a script can be delivered inside an
            // `sti; cli` pair that exists precisely to prevent that.
            case 0xFB: m.InterruptsEnabled = true; m.InterruptShadow = true; return;
            case 0xF8: m.Carry = false; return;
            case 0xF9: m.Carry = true; return;
            case 0xFC: m.Direction = false; return;
            case 0xFD: m.Direction = true; return;
            case 0xF5: m.Carry = !m.Carry; return;

            case 0xE8:
            {
                var rel = (short)FetchWord();
                m.Push(m.Ip);
                m.Ip = (ushort)(m.Ip + rel);
                m.RecordCall(m.Ip);
                return;
            }
            case 0xE9: { var rel = (short)FetchWord(); m.Ip = (ushort)(m.Ip + rel); return; }
            case 0xEB: { var rel = (sbyte)Fetch(); m.Ip = (ushort)(m.Ip + rel); return; }
            case 0xC3: m.Ip = m.Pop(); return;
            case 0xC2: { var extra = FetchWord(); m.Ip = m.Pop(); m.Registers[Machine8086.SP] += extra; return; }
            case 0xEA: { var offset = FetchWord(); var seg = FetchWord(); m.Ip = offset; m.Segments[Machine8086.CS] = seg; return; }

            // The far forms, which a booter never needs and a 142 KB EXE
            // cannot do without: it spans more than one code segment, so a
            // call between two of them is a far call and comes back far.
            case 0x9A:
            {
                var offset = FetchWord();
                var segment = FetchWord();
                m.Push(m.Segments[Machine8086.CS]);
                m.Push(m.Ip);
                m.Segments[Machine8086.CS] = segment;
                m.Ip = offset;
                m.RecordCall(m.Ip);
                return;
            }
            case 0xCB: m.Ip = m.Pop(); m.Segments[Machine8086.CS] = m.Pop(); return;
            case 0xCA:
            {
                var extra = FetchWord();
                m.Ip = m.Pop();
                m.Segments[Machine8086.CS] = m.Pop();
                m.Registers[Machine8086.SP] += extra;
                return;
            }

            case 0xCD: Interrupt(Fetch()); return;
            case 0xCF: m.Ip = m.Pop(); m.Segments[Machine8086.CS] = m.Pop(); m.Flags = m.Pop(); return;

            case 0x9C: m.Push(m.Flags); return;
            case 0x9D: m.Flags = m.Pop(); return;

            case 0x9E: m.Flags = (ushort)(m.Flags & 0xFF00 | ReadByteRegister(4)); return; // sahf
            case 0x9F: WriteByteRegister(4, (byte)m.Flags); return;                        // lahf
        }

        if (opcode is >= 0x50 and <= 0x57) { m.Push(m.Registers[opcode - 0x50]); return; }
        if (opcode is >= 0x58 and <= 0x5F) { m.Registers[opcode - 0x58] = m.Pop(); return; }
        if (opcode is 0x06 or 0x0E or 0x16 or 0x1E) { m.Push(m.Segments[opcode >> 3 & 3]); return; }
        if (opcode is 0x07 or 0x17 or 0x1F)
        {
            var into = opcode >> 3 & 3;
            m.Segments[into] = m.Pop();

            // `pop ss` opens the same one-instruction window as `mov ss,ax`,
            // and for the same reason: it is half of a stack switch, and an
            // interrupt between the halves pushes onto neither stack.
            if (into == Machine8086.SS) m.InterruptShadow = true;
            return;
        }

        if (opcode is >= 0xB0 and <= 0xB7) { WriteByteRegister(opcode - 0xB0, Fetch()); return; }
        if (opcode is >= 0xB8 and <= 0xBF) { m.Registers[opcode - 0xB8] = FetchWord(); return; }

        if (opcode is >= 0x40 and <= 0x47) { Increment(opcode - 0x40, 1); return; }
        if (opcode is >= 0x48 and <= 0x4F) { Increment(opcode - 0x48, -1); return; }

        if (opcode is >= 0x70 and <= 0x7F) { ConditionalJump(opcode); return; }

        // mov r/m, r and mov r, r/m
        if (opcode is 0x88 or 0x89 or 0x8A or 0x8B)
        {
            var wide = (opcode & 1) != 0;
            var toReg = (opcode & 2) != 0;
            var operand = DecodeModRm(Fetch(), out var reg);

            if (toReg)
            {
                WriteRegister(reg, wide, Read(operand, wide));
            }
            else
            {
                Write(operand, wide, ReadRegister(reg, wide));
            }

            return;
        }

        // mov r/m, sreg and mov sreg, r/m
        if (opcode is 0x8C or 0x8E)
        {
            var operand = DecodeModRm(Fetch(), out var reg);
            if (opcode == 0x8C)
            {
                Write(operand, true, m.Segments[reg & 3]);
            }
            else
            {
                m.Segments[reg & 3] = Read(operand, true);

                // Loading SS opens the same one-instruction window `sti`
                // does, so that `mov ss,ax / mov sp,bx` cannot be interrupted
                // between the two halves of a stack switch. Without it, an
                // interrupt was delivered with SS already moved and SP still
                // pointing into the old stack, and pushed onto neither.
                if ((reg & 3) == Machine8086.SS) m.InterruptShadow = true;
            }

            return;
        }

        if (opcode == 0x8D) // lea
        {
            var operand = DecodeModRm(Fetch(), out var reg);
            m.Registers[reg] = operand.Offset;
            return;
        }

        // The ALU block: add, or, adc, sbb, and, sub, xor, cmp.
        if (opcode < 0x40 && (opcode & 7) < 6)
        {
            AluOpcode(opcode);
            return;
        }

        if (opcode is 0x80 or 0x81 or 0x82 or 0x83) { AluImmediate(opcode); return; }
        if (opcode is 0x84 or 0x85) { TestOpcode(opcode); return; }
        if (opcode is 0x86 or 0x87) { ExchangeOpcode(opcode); return; }
        if (opcode is 0xC6 or 0xC7) { MoveImmediate(opcode); return; }
        if (opcode is >= 0xA0 and <= 0xA3) { AccumulatorMemory(opcode); return; }
        // A4-AF is not one block. A8 and A9 are `test al, imm8` and
        // `test ax, imm16`, sitting in the middle of the string opcodes, and
        // routing them to the string handler threw as soon as the cat
        // climbed - which is the first thing on that path that runs one.
        if (opcode is 0xA8) { Alu(4, ReadByteRegister(0), Fetch(), wide: false); return; }
        if (opcode is 0xA9) { Alu(4, m.Registers[Machine8086.AX], FetchWord(), wide: true); return; }
        if (opcode is >= 0xA4 and <= 0xAF) { StringOpcode(opcode, repeat); return; }
        if (opcode is >= 0xD0 and <= 0xD3) { ShiftOpcode(opcode); return; }
        if (opcode is 0xF6 or 0xF7) { Group3(opcode); return; }
        if (opcode is 0xFE or 0xFF) { Group45(opcode); return; }
        if (opcode is >= 0x91 and <= 0x97) { ExchangeWithAx(opcode); return; }

        switch (opcode)
        {
            case 0x98: WriteByteRegister(4, (byte)((ReadByteRegister(0) & 0x80) != 0 ? 0xFF : 0)); return; // cbw
            case 0x99: m.Registers[Machine8086.DX] = (ushort)((m.Registers[Machine8086.AX] & 0x8000) != 0 ? 0xFFFF : 0); return; // cwd
            case 0x37: Adjust(after: true); return;   // aaa
            case 0x3F: Adjust(after: false); return;  // aas
            case 0xE2: { var rel = (sbyte)Fetch(); if (--m.Registers[Machine8086.CX] != 0) m.Ip = (ushort)(m.Ip + rel); return; }
            case 0xE1: { var rel = (sbyte)Fetch(); if (--m.Registers[Machine8086.CX] != 0 && m.Zero) m.Ip = (ushort)(m.Ip + rel); return; }
            case 0xE0: { var rel = (sbyte)Fetch(); if (--m.Registers[Machine8086.CX] != 0 && !m.Zero) m.Ip = (ushort)(m.Ip + rel); return; }
            case 0xE3: { var rel = (sbyte)Fetch(); if (m.Registers[Machine8086.CX] == 0) m.Ip = (ushort)(m.Ip + rel); return; }
            case 0xE4: WriteByteRegister(0, ReadPort(Fetch())); return;
            case 0xE5: m.Registers[Machine8086.AX] = ReadWordPort(Fetch()); return;
            case 0xE6: WritePort(Fetch(), ReadByteRegister(0)); return;
            case 0xE7: WriteWordPort(Fetch(), m.Registers[Machine8086.AX]); return;

            // The port number is the whole of DX, not its low byte. Casting to
            // byte turned `in al, 3DAh` into a read of port 0DAh, so in_vblank
            // at 0x1491 always answered "not in retrace" and every one of its
            // fourteen callers span. It cost nothing on a static screen and
            // would have deadlocked the first frame of a running one.
            case 0xEC: WriteByteRegister(0, ReadPort(m.Registers[Machine8086.DX])); return;
            case 0xED: m.Registers[Machine8086.AX] = ReadWordPort(m.Registers[Machine8086.DX]); return;
            case 0xEE: WritePort(m.Registers[Machine8086.DX], ReadByteRegister(0)); return;
            case 0xEF: WriteWordPort(m.Registers[Machine8086.DX], m.Registers[Machine8086.AX]); return;
            case 0xC4: case 0xC5: LoadFarPointer(opcode); return;
            case 0xD7: WriteByteRegister(0, m.ReadByte(DataSegment, (ushort)(m.Registers[Machine8086.BX] + ReadByteRegister(0)))); return; // xlat
        }

        throw new NotSupportedException(
            $"opcode {opcode:X2} at {m.Segments[Machine8086.CS]:X4}:{m.Ip - 1:X4} is not implemented - " +
            "add it rather than letting the harness produce a plausible wrong frame");
    }

    private ushort ReadRegister(int index, bool wide) =>
        wide ? m.Registers[index] : ReadByteRegister(index);

    private void WriteRegister(int index, bool wide, ushort value)
    {
        if (wide)
        {
            m.Registers[index] = value;
        }
        else
        {
            WriteByteRegister(index, (byte)value);
        }
    }

    /// <summary>The port reads this game makes, answered plausibly and recorded.</summary>
    /// <remarks>
    /// <para>
    /// Port 40h used to return a hard zero so that <c>rng_seed</c> at
    /// <c>0x30C0</c> took its <c>0FA59h</c> fallback and matched the port's
    /// starting seed by accident. That was right for a static screen and wrong
    /// for a running game, because <c>read_timer</c> at <c>0x146D</c> reads
    /// the same counter to time the joystick axes and a counter that never
    /// moves is an input that never settles.
    /// </para>
    /// <para>
    /// It is a real counter now, so the original seeds itself from the clock -
    /// deterministically, because the clock is derived from the instruction
    /// count. The harness reads the seed back out of <c>[2D55h]</c> and hands
    /// it to the port rather than letting both sides guess.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The ports every 8086 machine has, whatever card is in it.
    /// </summary>
    /// <remarks>
    /// The clock, the interrupt controller and the keyboard controller are the
    /// same on a PC with a CGA and a PC with an EGA, so they stay here.
    /// Everything else is the machine's own and goes to
    /// <see cref="Machine8086.Peripherals"/>, whose default throws - because a
    /// silent plausible answer is how an unmodelled device becomes a frame that
    /// looks almost right.
    /// </remarks>
    private byte ReadPort(int port) => port switch
    {
        0x40 => RecordTimer(),
        0x60 => m.Scancode,

        // Bit 7 of the XT keyboard port is the acknowledge line an ISR
        // toggles. It reads back whatever was last written.
        0x61 => m.PortB,

        _ => m.Peripherals.Read(port),
    };

    private byte RecordTimer()
    {
        var value = m.Pit.Read();
        m.TimerReads.Add(value);
        return value;
    }

    /// <summary>
    /// The port writes, which are recorded and, for four ports, acted on.
    /// </summary>
    /// <remarks>
    /// Recording alone was enough while the harness only drew one screen.
    /// A running game needs these to actually do something: the 8253 has to
    /// accept the divisor the game programs into it, the PIC has to see the
    /// end-of-interrupt or no second key is ever delivered, and port 61h has to
    /// read back what was written or an ISR's acknowledge handshake reads a
    /// keyboard that never released the line.
    /// </remarks>
    /// <summary>
    /// A 16-bit <c>out</c>, which is TWO byte writes and not one.
    /// </summary>
    /// <remarks>
    /// AL goes to the port and AH to the one above it, which is the whole
    /// reason the instruction exists: the EGA's registers come in index/data
    /// pairs at 3C4h/3C5h and 3CEh/3CFh, so <c>out dx,ax</c> with
    /// <c>DX=3C4h</c> and <c>AX=0102h</c> selects the map mask and sets it to
    /// 1 in one instruction.
    ///
    /// Forwarding it as a single write handed a 16-bit value to a byte
    /// handler, which kept the index and threw the data away. The EGA
    /// executable has exactly that sequence at 0x21400, so the map mask stayed
    /// at 0Fh where the program had asked for 01h.
    /// </remarks>
    private void WriteWordPort(int port, ushort value)
    {
        WritePort(port, (byte)value);
        WritePort(Next(port), (byte)(value >> 8));
    }

    /// <summary>A 16-bit <c>in</c>, which is likewise two byte reads.</summary>
    private ushort ReadWordPort(int port) =>
        (ushort)(ReadPort(port) | ReadPort(Next(port)) << 8);

    /// <summary>
    /// The port above, wrapping inside the 16-bit I/O space.
    /// </summary>
    /// <remarks>
    /// The address space is 64 K and it wraps. Adding one to FFFFh gave port
    /// 10000h, which is not a port on any machine and reached a device model
    /// that had no business seeing it.
    /// </remarks>
    private static int Next(int port) => port + 1 & 0xFFFF;

    private void WritePort(int port, int value)
    {
        m.RecordPort(port, value);

        switch (port)
        {
            // A non-specific end-of-interrupt clears the HIGHEST-PRIORITY line
            // in service, which is the lowest-numbered one. A single flag
            // could not express that, and the practical consequence was that
            // IRQ 0 was blocked for the whole of a keyboard handler.
            case 0x20 when (value & 0x20) != 0:
                m.InterruptInService = false;
                for (var irq = 0; irq < 8; irq++)
                {
                    if ((m.InService >> irq & 1) == 0) continue;
                    m.InService &= ~(1 << irq);
                    break;
                }

                break;

            case 0x40:
                m.Pit.Write((byte)value);
                break;

            case 0x43:
                m.Pit.WriteMode((byte)value);
                break;

            case 0x61:
                m.PortB = (byte)value;
                break;

            // The interrupt mask, which IS acted on. A set bit is a masked
            // line, and a program writes FFh here precisely because it is
            // about to do something an interrupt would ruin.
            case 0x21:
                m.InterruptMask = (byte)value;
                break;

            // Recorded but not acted on: a non-EOI command to the controller
            // and the speaker's own counter change nothing a comparison looks
            // at.
            case 0x20:
            case 0x42:
            case 0xA0: // NMI mask
                break;

            default:
                m.Peripherals.Write(port, value);
                break;
        }
    }

    /// <summary>Charge the clock for the iterations past the first.</summary>
    private void Charge(int iterations)
    {
        for (var i = 1; i < iterations; i++)
        {
            for (var c = 0; c < RepeatCost; c++)
            {
                m.Pit.Step();
            }
        }
    }

    private void LoadFarPointer(byte opcode)
    {
        var operand = DecodeModRm(Fetch(), out var reg);
        m.Registers[reg] = m.ReadWord(operand.Segment, operand.Offset);
        m.Segments[opcode == 0xC4 ? Machine8086.ES : Machine8086.DS] =
            m.ReadWord(operand.Segment, (ushort)(operand.Offset + 2));
    }

    private void ExchangeWithAx(byte opcode)
    {
        var index = opcode - 0x90;
        (m.Registers[Machine8086.AX], m.Registers[index]) = (m.Registers[index], m.Registers[Machine8086.AX]);
    }

    private void ExchangeOpcode(byte opcode)
    {
        var wide = (opcode & 1) != 0;
        var operand = DecodeModRm(Fetch(), out var reg);
        var a = Read(operand, wide);
        var b = ReadRegister(reg, wide);
        Write(operand, wide, b);
        WriteRegister(reg, wide, a);
    }

    /// <summary>
    /// <c>aaa</c> and <c>aas</c> - the decimal adjust the score depends on.
    /// </summary>
    private void Adjust(bool after)
    {
        var al = ReadByteRegister(0);
        if ((al & 0x0F) > 9 || m.Auxiliary)
        {
            WriteByteRegister(0, (byte)((al + (after ? 6 : -6)) & 0x0F));
            WriteByteRegister(4, (byte)(ReadByteRegister(4) + (after ? 1 : -1)));
            m.Auxiliary = true;
            m.Carry = true;
        }
        else
        {
            WriteByteRegister(0, (byte)(al & 0x0F));
            m.Auxiliary = false;
            m.Carry = false;
        }
    }

    private void MoveImmediate(byte opcode)
    {
        var wide = (opcode & 1) != 0;
        var operand = DecodeModRm(Fetch(), out _);
        Write(operand, wide, wide ? FetchWord() : Fetch());
    }

    private void AccumulatorMemory(byte opcode)
    {
        var offset = FetchWord();
        switch (opcode)
        {
            case 0xA0: WriteByteRegister(0, m.ReadByte(DataSegment, offset)); return;
            case 0xA1: m.Registers[Machine8086.AX] = m.ReadWord(DataSegment, offset); return;
            case 0xA2: m.WriteByte(DataSegment, offset, ReadByteRegister(0)); return;
            default: m.WriteWord(DataSegment, offset, m.Registers[Machine8086.AX]); return;
        }
    }

    private void TestOpcode(byte opcode)
    {
        var wide = (opcode & 1) != 0;
        var operand = DecodeModRm(Fetch(), out var reg);
        var result = Read(operand, wide) & ReadRegister(reg, wide);
        m.SetResultFlags(result, wide);
        m.Carry = false;
        m.Overflow = false;
    }

    private void AluOpcode(byte opcode)
    {
        var operation = opcode >> 3 & 7;
        var wide = (opcode & 1) != 0;
        var form = opcode & 7;

        if (form is 4 or 5)
        {
            // Accumulator with an immediate.
            var immediate = form == 5 ? FetchWord() : Fetch();
            var accumulator = ReadRegister(Machine8086.AX, form == 5);
            var value = Alu(operation, accumulator, immediate, form == 5);
            if (operation != 7)
            {
                WriteRegister(Machine8086.AX, form == 5, value);
            }

            return;
        }

        var toReg = (opcode & 2) != 0;
        var operand = DecodeModRm(Fetch(), out var reg);
        var left = toReg ? ReadRegister(reg, wide) : Read(operand, wide);
        var right = toReg ? Read(operand, wide) : ReadRegister(reg, wide);
        var outcome = Alu(operation, left, right, wide);

        if (operation == 7)
        {
            return; // cmp discards
        }

        if (toReg)
        {
            WriteRegister(reg, wide, outcome);
        }
        else
        {
            Write(operand, wide, outcome);
        }
    }

    private void AluImmediate(byte opcode)
    {
        var wide = (opcode & 1) != 0;
        var operand = DecodeModRm(Fetch(), out var operation);
        var immediate = opcode switch
        {
            0x81 => FetchWord(),
            0x83 => (ushort)(short)(sbyte)Fetch(),
            _ => Fetch(),
        };

        var value = Alu(operation, Read(operand, wide), immediate, wide);
        if (operation != 7)
        {
            Write(operand, wide, value);
        }
    }

    private ushort Alu(int operation, ushort left, ushort right, bool wide)
    {
        var mask = wide ? 0xFFFF : 0xFF;
        var top = wide ? 0x8000 : 0x80;
        int result;

        switch (operation)
        {
            case 0: // add
            case 2: // adc
                var carryIn = operation == 2 && m.Carry ? 1 : 0;
                result = left + right + carryIn;
                m.Carry = (result & ~mask) != 0;
                m.Overflow = ((left ^ result) & (right ^ result) & top) != 0;
                m.Auxiliary = ((left ^ right ^ result) & 0x10) != 0;
                break;

            case 1: result = left | right; m.Carry = m.Overflow = false; break;
            case 4: result = left & right; m.Carry = m.Overflow = false; break;
            case 6: result = left ^ right; m.Carry = m.Overflow = false; break;

            default: // sub, sbb, cmp
                var borrow = operation == 3 && m.Carry ? 1 : 0;
                result = left - right - borrow;
                m.Carry = (result & ~mask) != 0;
                m.Overflow = ((left ^ right) & (left ^ result) & top) != 0;
                m.Auxiliary = ((left ^ right ^ result) & 0x10) != 0;
                break;
        }

        m.SetResultFlags(result, wide);
        return (ushort)(result & mask);
    }

    private void ShiftOpcode(byte opcode)
    {
        var wide = (opcode & 1) != 0;
        var byCl = (opcode & 2) != 0;
        var operand = DecodeModRm(Fetch(), out var operation);
        var count = byCl ? ReadByteRegister(1) : 1;

        var value = Read(operand, wide);
        var bits = wide ? 16 : 8;
        var top = wide ? 0x8000 : 0x80;

        for (var i = 0; i < count; i++)
        {
            var carry = m.Carry;
            switch (operation)
            {
                case 0: m.Carry = (value & top) != 0; value = (ushort)(value << 1 | (m.Carry ? 1 : 0)); break;  // rol
                case 1: m.Carry = (value & 1) != 0; value = (ushort)(value >> 1 | (m.Carry ? top : 0)); break;  // ror
                case 2: var c = (value & top) != 0; value = (ushort)(value << 1 | (carry ? 1 : 0)); m.Carry = c; break; // rcl
                case 3: var b = (value & 1) != 0; value = (ushort)(value >> 1 | (carry ? top : 0)); m.Carry = b; break; // rcr
                case 4: case 6: m.Carry = (value & top) != 0; value = (ushort)(value << 1); break;              // shl
                case 5: m.Carry = (value & 1) != 0; value = (ushort)(value >> 1); break;                        // shr
                default:                                                                                         // sar
                    m.Carry = (value & 1) != 0;
                    value = (ushort)((short)(wide ? value : (ushort)(sbyte)value) >> 1);
                    break;
            }

            value &= (ushort)(wide ? 0xFFFF : 0xFF);
        }

        if (count > 0 && operation >= 4)
        {
            m.SetResultFlags(value, wide);
        }

        Write(operand, wide, value);
    }

    private void Group3(byte opcode)
    {
        var wide = (opcode & 1) != 0;
        var operand = DecodeModRm(Fetch(), out var operation);

        switch (operation)
        {
            case 0:
            case 1:
            {
                var immediate = wide ? FetchWord() : Fetch();
                var result = Read(operand, wide) & immediate;
                m.SetResultFlags(result, wide);
                m.Carry = m.Overflow = false;
                return;
            }

            case 2: Write(operand, wide, (ushort)~Read(operand, wide)); return; // not - no flags
            case 3:
            {
                var value = Read(operand, wide);
                var result = Alu(5, 0, value, wide); // neg is 0 - value
                Write(operand, wide, result);
                return;
            }

            case 4: // mul
            {
                if (wide)
                {
                    var product = (uint)m.Registers[Machine8086.AX] * Read(operand, true);
                    m.Registers[Machine8086.AX] = (ushort)product;
                    m.Registers[Machine8086.DX] = (ushort)(product >> 16);
                    m.Carry = m.Overflow = m.Registers[Machine8086.DX] != 0;
                }
                else
                {
                    var product = ReadByteRegister(0) * (byte)Read(operand, false);
                    m.Registers[Machine8086.AX] = (ushort)product;
                    m.Carry = m.Overflow = (product & 0xFF00) != 0;
                }

                return;
            }

            case 6: // div
            {
                if (wide)
                {
                    var dividend = (uint)m.Registers[Machine8086.DX] << 16 | m.Registers[Machine8086.AX];
                    var divisor = Read(operand, true);
                    if (divisor == 0) return;
                    m.Registers[Machine8086.AX] = (ushort)(dividend / divisor);
                    m.Registers[Machine8086.DX] = (ushort)(dividend % divisor);
                }
                else
                {
                    var divisor = (byte)Read(operand, false);
                    if (divisor == 0) return;
                    var dividend = m.Registers[Machine8086.AX];
                    WriteByteRegister(0, (byte)(dividend / divisor));
                    WriteByteRegister(4, (byte)(dividend % divisor));
                }

                return;
            }

            default:
                throw new NotSupportedException($"group 3 operation {operation} is not implemented");
        }
    }

    private void Group45(byte opcode)
    {
        var wide = (opcode & 1) != 0;
        var operand = DecodeModRm(Fetch(), out var operation);

        switch (operation)
        {
            case 0:
            case 1:
            {
                var value = Read(operand, wide);
                var result = operation == 0 ? value + 1 : value - 1;
                Write(operand, wide, (ushort)result);
                m.SetIncDecFlags(value, result, wide, decrement: operation == 1);
                return;
            }

            // Call near indirect. THE TARGET IS READ FIRST, because the
            // push can overwrite it: `call word ptr [bp]` with BP = SP-2
            // stores the return address over the very word being called
            // through, and the machine then jumps to the return address.
            // Carried over from the sibling harness with the order wrong;
            // the far form added on this branch already had it right.
            case 2:
            {
                var target = Read(operand, true);
                m.Push(m.Ip);
                m.Ip = target;
                return;
            }

            // Jump near indirect. Same reasoning does not apply - nothing is
            // pushed - but it is read the same way for consistency.
            
            case 4: m.Ip = Read(operand, true); return;                            // jmp near indirect

            // Call far indirect, which is how an interrupt handler chains to
            // the one it replaced: the address it saved is in memory, so the
            // call cannot be a direct one. Alley Cat's handlers never chained.
            case 3:
            {
                // Low word then high, as the bus does - and as case 5 does,
                // which matters because a read can have a side effect.
                var offset = m.ReadWord(operand.Segment, operand.Offset);
                var segment = m.ReadWord(operand.Segment, (ushort)(operand.Offset + 2));
                m.Push(m.Segments[Machine8086.CS]);
                m.Push(m.Ip);
                m.Segments[Machine8086.CS] = segment;
                m.Ip = offset;
                m.RecordCall(m.Ip);
                return;
            }

            case 5:
                m.Ip = m.ReadWord(operand.Segment, operand.Offset);
                m.Segments[Machine8086.CS] = m.ReadWord(operand.Segment, (ushort)(operand.Offset + 2));
                return;
            case 6: m.Push(Read(operand, true)); return;
            default:
                throw new NotSupportedException($"group 5 operation {operation} is not implemented");
        }
    }

    /// <summary>
    /// What one iteration of a repeated string operation costs, as a multiple
    /// of an average instruction.
    /// </summary>
    /// <remarks>
    /// A <c>rep movsw</c> with <c>CX = 8192</c> moves 16 KB. Charging it as
    /// one instruction made a whole-screen blit take 2.5 microseconds, which
    /// put every screen build on the wrong side of the timer and retrace
    /// boundaries it is gated on. On an 8088 a <c>movsw</c> iteration is
    /// about 25 cycles against roughly 12 for an average instruction, so each
    /// iteration past the first is charged as two.
    ///
    /// This is still an approximation - there is no prefetch queue, no bus
    /// contention and no DRAM refresh here - but it is an approximation that
    /// is out by a factor of two rather than by a factor of a thousand.
    /// </remarks>
    public const int RepeatCost = 2;

    private void StringOpcode(byte opcode, int repeat)
    {
        var iterations = 0;
        var wide = (opcode & 1) != 0;
        var step = (wide ? 2 : 1) * (m.Direction ? -1 : 1);

        while (true)
        {
            if (repeat != 0 && m.Registers[Machine8086.CX] == 0)
            {
                Charge(iterations);
                return;
            }

            iterations++;
            var si = m.Registers[Machine8086.SI];
            var di = m.Registers[Machine8086.DI];

            switch (opcode & 0xFE)
            {
                case 0xA4: // movs
                    if (wide) m.WriteWord(m.Segments[Machine8086.ES], di, m.ReadWord(DataSegment, si));
                    else m.WriteByte(m.Segments[Machine8086.ES], di, m.ReadByte(DataSegment, si));
                    m.Registers[Machine8086.SI] = (ushort)(si + step);
                    m.Registers[Machine8086.DI] = (ushort)(di + step);
                    break;

                case 0xAA: // stos
                    if (wide) m.WriteWord(m.Segments[Machine8086.ES], di, m.Registers[Machine8086.AX]);
                    else m.WriteByte(m.Segments[Machine8086.ES], di, ReadByteRegister(0));
                    m.Registers[Machine8086.DI] = (ushort)(di + step);
                    break;

                case 0xAC: // lods
                    if (wide) m.Registers[Machine8086.AX] = m.ReadWord(DataSegment, si);
                    else WriteByteRegister(0, m.ReadByte(DataSegment, si));
                    m.Registers[Machine8086.SI] = (ushort)(si + step);
                    break;

                case 0xAE: // scas
                {
                    var value = wide ? m.ReadWord(m.Segments[Machine8086.ES], di) : m.ReadByte(m.Segments[Machine8086.ES], di);
                    Alu(7, ReadRegister(Machine8086.AX, wide), value, wide);
                    m.Registers[Machine8086.DI] = (ushort)(di + step);
                    break;
                }

                case 0xA6: // cmps
                {
                    var left = wide ? m.ReadWord(DataSegment, si) : m.ReadByte(DataSegment, si);
                    var right = wide ? m.ReadWord(m.Segments[Machine8086.ES], di) : m.ReadByte(m.Segments[Machine8086.ES], di);
                    Alu(7, left, right, wide);
                    m.Registers[Machine8086.SI] = (ushort)(si + step);
                    m.Registers[Machine8086.DI] = (ushort)(di + step);
                    break;
                }

                default:
                    throw new NotSupportedException($"string opcode {opcode:X2} is not implemented");
            }

            if (repeat == 0)
            {
                Charge(iterations);
                return;
            }

            m.Registers[Machine8086.CX]--;

            // repe and repne only apply to the comparing forms.
            if ((opcode & 0xFE) is 0xAE or 0xA6)
            {
                if (repeat == 3 && !m.Zero) { Charge(iterations); return; }
                if (repeat == 2 && m.Zero) { Charge(iterations); return; }
            }
        }
    }

    private void Increment(int register, int delta)
    {
        var before = m.Registers[register];
        var result = before + delta;
        m.Registers[register] = (ushort)result;
        m.SetIncDecFlags(before, result, wide: true, decrement: delta < 0);
    }

    private void ConditionalJump(byte opcode)
    {
        var rel = (sbyte)Fetch();
        var take = (opcode & 0x0E) switch
        {
            0x00 => m.Overflow,
            0x02 => m.Carry,
            0x04 => m.Zero,
            0x06 => m.Carry || m.Zero,
            0x08 => m.Sign,
            0x0A => m.Parity,
            0x0C => m.Sign != m.Overflow,
            _ => m.Sign != m.Overflow || m.Zero,
        };

        if ((opcode & 1) != 0)
        {
            take = !take;
        }

        if (take)
        {
            m.Ip = (ushort)(m.Ip + rel);
        }
    }

    private void Interrupt(byte number)
    {
        // Via the recorder, not the list: a wait loop takes INT 1Ah millions
        // of times and an unbounded list turns a diagnostic into an
        // out-of-memory, which is what RecordLimit exists to stop.
        m.RecordInterrupt(number, m.Registers[Machine8086.AX]);

        // Anything the PROGRAM has hooked is vectored for real, and that test
        // comes FIRST.
        //
        // A software `int` was being swallowed like a BIOS call in the Alley
        // Cat harness, and that hid a whole keyboard path: an `int 9` inside
        // the program is how a PCjr's INT 48h reaches the game's own handler.
        // Returning early meant the handler never ran and 134 interrupts were
        // delivered to nothing at all.
        //
        // The test is where the vector points. A BIOS or DOS service still
        // points outside the program and is still answered here; a hooked one
        // points into the loaded image and is not ours to answer. Rockford
        // hooks the timer and the keyboard and then calls INT 21h through the
        // vectors DOS installed, so both halves of that rule are load-bearing
        // in the same run.
        if (m.HookedByProgram(number))
        {
            // A software `int` is not a hardware interrupt and must not claim
            // the controller. Letting it set the in-service flag delivered
            // exactly one key and then stopped for ever, because a handler
            // reached this way never writes an EOI to clear it.
            var inService = m.InterruptInService;
            m.Raise(number);
            m.InterruptInService = inService;
            return;
        }

        switch (number)
        {
            case 0x1A:
                // AH=0: return the tick count in CX:DX. This is the BIOS
                // reading 0040:006Ch, which IRQ 0 bumps - so it is the 8253's
                // wrap count and nothing else, and it speeds up when a game
                // reprograms the divisor.
                if (ReadByteRegister(4) == 0)
                {
                    // FROM THE BIOS DATA AREA, which is where a real INT 1Ah
                    // reads it from. Answering from the PIT wrap count made
                    // the service disagree with the memory the timer handler
                    // maintains: with interrupts disabled the tick stood still
                    // and this still counted up.
                    var low = m.BiosTickOverride
                              ?? m.ReadWord(0, Machine8086.BiosTickAddress);
                    var high = m.BiosTickOverride is null
                        ? m.ReadWord(0, Machine8086.BiosTickAddress + 2)
                        : (ushort)0;

                    m.Registers[Machine8086.CX] = high;
                    m.Registers[Machine8086.DX] = low;
                    m.TickReads.Add(low);
                }

                return;

            case 0x11:
                // Equipment list. Bits 4-5 as 10b: 80x25 colour.
                m.Registers[Machine8086.AX] = m.Equipment;
                return;

            default:
                // The BIOS and DOS this machine is wearing. Unlike the ports,
                // an unanswered service is NOT fatal by default: INT 16h and
                // the parts of INT 10h that only move a cursor change nothing
                // a comparison looks at, and throwing on them would mean
                // modelling a text BIOS to run a graphics game. Whether a
                // given service is worth modelling is the machine's call, so
                // the machine gets to make it.
                m.Services.Service(number, m);
                return;
        }
    }
}
