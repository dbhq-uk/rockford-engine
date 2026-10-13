namespace Rockford.Reference;

/// <summary>
/// Enough of DOS and the video BIOS to run one 1988 game.
/// </summary>
/// <remarks>
/// <para>
/// The Alley Cat harness needed none of this: that game is a booter, so the
/// boot sector puts one payload in memory and the program owns the machine from
/// the first instruction. Rockford is an ordinary <c>.EXE</c>, so before any of
/// its code runs there has to be a program segment prefix, a load segment, a
/// stack, and an INT 21h that can open a file.
/// </para>
/// <para>
/// <b>Everything not implemented throws, with the function number in the
/// message.</b> That is deliberate and it is how the list below got written: the
/// first run stopped at the first service the game wanted, which was cheaper and
/// more truthful than reading the disassembly for calls and guessing. A DOS that
/// returns "success" for a function it does not implement produces a game that
/// runs and is wrong, which is the one outcome this harness exists to prevent.
/// </para>
/// </remarks>
public sealed class Dos : IServices
{
    /// <summary>Where the PSP goes - a plausible spot below a small DOS.</summary>
    public const ushort PspSegment = 0x0100;

    /// <summary>The image starts one paragraph-sized PSP later.</summary>
    public const ushort LoadSegment = PspSegment + 0x10;

    private readonly Machine8086 _machine;
    private readonly List<string> _volume = [];
    private readonly Dictionary<ushort, OpenFile> _handles = [];
    private ushort _nextHandle = 5;

    /// <summary>The allocation arena, as a paragraph that only ever goes up.</summary>
    private ushort _arena;

    /// <summary>Blocks handed out, so a free or a resize can be checked.</summary>
    private readonly HashSet<ushort> _blocks = [];

    /// <summary>Every service call taken, so a run can be accounted for.</summary>
    public List<(byte Interrupt, ushort Ax)> Calls { get; } = [];

    /// <summary>Files the program opened, in order, including the ones it missed.</summary>
    public List<string> Opened { get; } = [];

    /// <summary>Where each of those names was, which fixes the data segment.</summary>
    public List<(ushort Segment, ushort Offset)> OpenedFrom { get; } = [];

    /// <summary>The exit code, once the program has asked to terminate.</summary>
    public int? Exited { get; private set; }

    private sealed class OpenFile(string path, byte[] data)
    {
        public string Path { get; } = path;
        public byte[] Data { get; } = data;
        public int Position { get; set; }
    }

    public Dos(Machine8086 machine, params string[] directories)
    {
        _machine = machine;
        _volume.AddRange(directories);
    }

    /// <summary>
    /// Load an <c>.EXE</c> the way DOS does and point the machine at it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ROCKFORD.EXE</c> is 29,963 bytes with a 512-byte header and
    /// <b>no relocation entries at all</b>, which looks wrong for a 142 KB
    /// program until you notice it is EXEPACK-packed: the packer keeps its own
    /// relocation table inside the stub and the stub applies it at run time.
    /// </para>
    /// <para>
    /// So this loads the packed file and lets the game unpack itself. That is
    /// the better of the two options anyway - <c>tools/unexepack.py</c> could
    /// hand over a ready-made image, but then the unpacking would be our code,
    /// and the first thing the harness ran would be the first thing it had not
    /// verified.
    /// </para>
    /// </remarks>
    public void Load(byte[] exe)
    {
        if (exe[0] != 'M' || exe[1] != 'Z')
        {
            throw new InvalidOperationException("not an MZ executable");
        }

        var pages = Read16(exe, 4);
        var lastPage = Read16(exe, 2);
        var relocations = Read16(exe, 6);
        var headerParagraphs = Read16(exe, 8);
        var minimumParagraphs = Read16(exe, 10);
        var ss = Read16(exe, 14);
        var sp = Read16(exe, 16);
        var ip = Read16(exe, 20);
        var cs = Read16(exe, 22);
        var relocationTable = Read16(exe, 24);

        var header = headerParagraphs * 16;
        var length = (pages - 1) * 512 + (lastPage == 0 ? 512 : lastPage) - header;

        Array.Copy(exe, header, _machine.Memory, LoadSegment << 4, length);

        // Present for completeness. Rockford has none, but a harness that
        // silently ignored a relocation table would load a differently packed
        // build to the wrong addresses and then blame the port.
        for (var i = 0; i < relocations; i++)
        {
            var offset = Read16(exe, relocationTable + i * 4);
            var segment = Read16(exe, relocationTable + i * 4 + 2);
            var at = ((LoadSegment + segment) << 4) + offset;
            var patched = (ushort)(Read16(_machine.Memory, at) + LoadSegment);
            _machine.Memory[at] = (byte)patched;
            _machine.Memory[at + 1] = (byte)(patched >> 8);
        }

        _machine.LoadSegment = LoadSegment;

        var paragraphs = (length + 15) / 16 + minimumParagraphs;
        _machine.LoadParagraphs = (ushort)paragraphs;
        _arena = (ushort)(LoadSegment + paragraphs + 1);

        // The program's own allocation is a block like any other. Leaving it
        // out meant a program that freed its own PSP - which is what a .EXE
        // does on the way out - was refused, while resize accepted it.
        _blocks.Add(PspSegment);

        // After the arena, because the PSP advertises where the program's
        // memory ends and that is the arena's floor.
        BuildInterruptTable();
        BuildPsp();

        _machine.Segments[Machine8086.CS] = (ushort)(LoadSegment + cs);
        _machine.Ip = ip;
        _machine.Segments[Machine8086.SS] = (ushort)(LoadSegment + ss);
        _machine.Registers[Machine8086.SP] = sp;

        // DS and ES point at the PSP on entry, not at the program's data. The
        // program sets its own DS, and a harness that helpfully pre-set it
        // would hide the instruction that does so.
        _machine.Segments[Machine8086.DS] = PspSegment;
        _machine.Segments[Machine8086.ES] = PspSegment;

        _machine.Services = this;
    }

    /// <summary>The segment the unpacked program runs its code in.</summary>
    public ushort? CodeSegment { get; private set; }

    /// <summary>Where the stub handlers live, as a ROM BIOS would.</summary>
    public const ushort BiosSegment = 0xF000;

    /// <summary>
    /// Fill the interrupt table with handlers that are not the program's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A zeroed interrupt table is not a plausible machine, and this cost a
    /// day's worth of looking in the wrong place.</b> At file offset
    /// <c>0x21392</c> the game reads INT 8's vector, compares its segment with
    /// the one it saved when it last hooked the timer, and skips installing its
    /// handler when they match:
    /// </para>
    /// <code>
    ///   21392: push ds
    ///   21393: mov  ah,35h      ; get vector
    ///   21395: mov  al,08h      ; INT 8
    ///   21397: int  21h
    ///   21399: mov  ax,es
    ///   2139b: cmp  ax,cs:[12h] ; already ours?
    ///   213a0: je   213cc       ; yes - do not install it twice
    /// </code>
    /// <para>
    /// Its saved segment starts at zero, and an empty table answers zero, so
    /// the two matched and the game concluded it had already hooked the timer.
    /// It then programmed the 8253 to <c>4DAEh</c> - 60.0 Hz, exactly as
    /// <c>docs/PORT-PLAN.md</c> says - and sat in its frame pacer at
    /// <c>0x1D87E</c> waiting for a counter that nothing was going to
    /// increment, 1,497,175 times.
    /// </para>
    /// <para>
    /// The check is a perfectly sensible one against a real machine, where INT
    /// 8 points into ROM at F000 and never at 0000. Same lesson as the ports:
    /// an unmodelled device that answers zero produces a program that runs and
    /// is wrong.
    /// </para>
    /// </remarks>
    private void BuildInterruptTable()
    {
        var rom = BiosSegment << 4;

        for (var vector = 0; vector < 256; vector++)
        {
            // One stub each, THIRTY-TWO bytes apart. Eight was enough while
            // every stub was an IRET and only the timer was longer; a keyboard
            // handler that actually acknowledges the hardware is nineteen
            // bytes and would have run into vector 10's.
            var offset = (ushort)(0x1000 + vector * StubStride);
            _machine.WriteWord(0, (ushort)(vector * 4), offset);
            _machine.WriteWord(0, (ushort)(vector * 4 + 2), BiosSegment);

            // iret, and nothing else. These only execute when the PROGRAM
            // chains to the handler it replaced; a service call is answered
            // before it gets here.
            _machine.Memory[rom + offset] = 0xCF;
        }

        // The timer. A chaining handler expects the real one to acknowledge
        // the controller and to bump the BIOS tick at 0040:006Ch.
        //
        // AND IT SAVES AX, which the first version did not. `mov al,20h` for
        // the end-of-interrupt destroyed AL in whatever the program was doing,
        // and a handler that does that is not a BIOS, it is a bug with a
        // vector. What it cost: the VGA build's .SCM decoder reads a control
        // byte into AL and branches on it, so an interrupt between the read
        // and the branch turned a run of two into a literal run of THIRTY-TWO
        // - 20h - and put the compressed stream on screen as pixels. The EGA
        // build never showed it; its screens are stored raw.
        // AND IT SETS DS, which the first version did not. `inc word [046Ch]`
        // is DS-relative and DS is whatever the interrupted program had, so
        // the stub incremented a word 046Ch into the GUEST'S OWN DATA and left
        // the real BIOS tick at zero. A running VGA title visited it 19 times
        // and 0040:006Ch never moved. Writing into the program you are
        // measuring is the worst thing a harness can do, because the
        // corruption is indistinguishable from a port defect.
        Stub(0x08,
            0x50,                                // push ax
            0x1E,                                // push ds
            0x31, 0xC0,                          // xor ax,ax
            0x8E, 0xD8,                          // mov ds,ax
            0xFF, 0x06, 0x6C, 0x04,              // inc word [046Ch]
            0x75, 0x04,                          // jnz over the high word
            0xFF, 0x06, 0x6E, 0x04,              // inc word [046Eh]
            0xB0, 0x20,                          // mov al,20h
            0xE6, 0x20,                          // out 20h,al
            0x1F,                                // pop ds
            0x58,                                // pop ax
            0xCF);                               // iret

        // The keyboard, which is NOT optional and was a bare IRET.
        //
        // The VGA build's own handler chains here through a far call at
        // 0x11919. A stub that only returns never writes the end-of-interrupt,
        // so the controller stays in service and NO SECOND KEY IS EVER
        // DELIVERED - one keyboard interrupt in three million instructions.
        //
        // It reads the scancode, performs the port 61h acknowledge handshake
        // an XT keyboard needs, and writes the EOI.
        Stub(0x09,
            0x50,                                // push ax
            0xE4, 0x60,                          // in al,60h
            0xE4, 0x61,                          // in al,61h
            0x0C, 0x80,                          // or al,80h
            0xE6, 0x61,                          // out 61h,al
            0x24, 0x7F,                          // and al,7Fh
            0xE6, 0x61,                          // out 61h,al
            0xB0, 0x20,                          // mov al,20h
            0xE6, 0x20,                          // out 20h,al
            0x58,                                // pop ax
            0xCF);                               // iret
    }

    /// <summary>Bytes reserved for each stub handler.</summary>
    private const int StubStride = 32;

    private void Stub(int vector, params byte[] code)
    {
        if (code.Length > StubStride)
        {
            throw new InvalidOperationException(
                $"the INT {vector:X2}h stub is {code.Length} bytes and the stride is {StubStride}");
        }

        code.CopyTo(_machine.Memory, (BiosSegment << 4) + 0x1000 + vector * StubStride);
    }

    private void BuildPsp()
    {
        var psp = PspSegment << 4;

        // INT 20h at the top, the classic way a .COM returns. Nothing calls it
        // here, but the bytes are what a program sees if it looks.
        _machine.Memory[psp] = 0xCD;
        _machine.Memory[psp + 1] = 0x20;

        // The first paragraph past the program, which is what a program reads
        // to find out how much memory it was given. DERIVED from the image and
        // its minimum allocation rather than a round number that happened to be
        // big enough for one of the two builds.
        var top = _arena;
        _machine.Memory[psp + 2] = (byte)top;
        _machine.Memory[psp + 3] = (byte)(top >> 8);

        // An empty command tail: a length byte of zero and a carriage return.
        _machine.Memory[psp + 0x80] = 0;
        _machine.Memory[psp + 0x81] = 0x0D;
    }

    private static ushort Read16(byte[] data, int at) => (ushort)(data[at] | data[at + 1] << 8);

    /// <summary>Find a file on the volume, case-insensitively, 8.3.</summary>
    /// <remarks>
    /// The volume is a list of directories searched in order, because the EGA
    /// release ships on two disks and the game asks for the second one part way
    /// through. Presenting both at once means no swap prompt, and the files are
    /// the same files either way - but it IS a difference from the original's
    /// situation and it is recorded here rather than left to be discovered.
    /// </remarks>
    private string? Find(string name)
    {
        foreach (var directory in _volume)
        {
            foreach (var candidate in Directory.EnumerateFiles(directory))
            {
                if (string.Equals(Path.GetFileName(candidate), name, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Read an ASCIIZ string out of the machine's memory.</summary>
    private string StringAt(ushort segment, ushort offset)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < 128; i++)
        {
            var b = _machine.ReadByte(segment, (ushort)(offset + i));
            if (b == 0) break;
            text.Append((char)b);
        }

        return text.ToString();
    }

    public bool Service(byte number, Machine8086 machine)
    {
        // The first service call is the first moment the program is
        // demonstrably running its OWN code rather than the unpacker's, so
        // this is where its code segment can be read rather than guessed.
        //
        // It cannot be a constant. EXEPACK unpacks to a different base in each
        // build - 1CFBh in the EGA one and 0E9Fh in the VGA one, relative to
        // the load segment - and a hard-coded paragraph produced the right
        // LINEAR address by accident while giving every routine the wrong CS.
        // That is invisible until a routine reads `cs:`-relative data, and
        // then it is a wrong answer rather than a crash.
        CodeSegment ??= machine.Segments[Machine8086.CS];

        Calls.Add((number, machine.Registers[Machine8086.AX]));

        return number switch
        {
            0x21 => Dos21(),
            0x10 => Bios10(),

            0x16 => Keyboard(),

            // Terminate, the .COM way.
            0x20 => Terminate(0),

            _ => throw new NotSupportedException(
                $"INT {number:X2}h at {machine.Segments[Machine8086.CS]:X4}:{machine.Ip:X4} "
                + "is not modelled"),
        };
    }

    private ushort Ax
    {
        get => _machine.Registers[Machine8086.AX];
        set => _machine.Registers[Machine8086.AX] = value;
    }

    private byte Ah => (byte)(Ax >> 8);

    private byte Al => (byte)Ax;

    private ushort Bx => _machine.Registers[Machine8086.BX];

    private ushort Cx => _machine.Registers[Machine8086.CX];

    private ushort Dx => _machine.Registers[Machine8086.DX];

    private void Succeed(ushort ax = 0)
    {
        Ax = ax;
        _machine.Carry = false;
    }

    private void Fail(ushort code)
    {
        Ax = code;
        _machine.Carry = true;
    }

    private bool Terminate(int code)
    {
        Exited = code;
        _machine.Halt();
        return true;
    }

    private bool Dos21()
    {
        switch (Ah)
        {
            case 0x00:
                return Terminate(0);

            case 0x4C:
                return Terminate(Al);

            // Get and set an interrupt vector. The game hooks the timer and the
            // keyboard through these, and the interpreter's "is it hooked"
            // test reads the same table, so these two have to actually write
            // it rather than pretend.
            case 0x25:
                Hooked.Add(Al);
                _machine.WriteWord(0, (ushort)(Al * 4), Dx);
                _machine.WriteWord(0, (ushort)(Al * 4 + 2), _machine.Segments[Machine8086.DS]);
                Succeed();
                return true;

            case 0x35:
                _machine.Registers[Machine8086.BX] = _machine.ReadWord(0, (ushort)(Al * 4));
                _machine.Segments[Machine8086.ES] = _machine.ReadWord(0, (ushort)(Al * 4 + 2));
                Succeed();
                return true;

            // DOS 3.30, which is what a 1988 game would have met.
            case 0x30:
                Ax = 0x1E03;
                _machine.Registers[Machine8086.BX] = 0;
                _machine.Registers[Machine8086.CX] = 0;
                return true;

            case 0x19: // current drive, A:
                Ax = (ushort)(Ax & 0xFF00);
                return true;

            case 0x0E: // select drive, and how many there are
                Ax = (ushort)(Ax & 0xFF00 | 2);
                return true;

            case 0x1A: // set the disk transfer address
                _dtaSegment = _machine.Segments[Machine8086.DS];
                _dtaOffset = Dx;
                return true;

            case 0x3D:
                return Open();

            case 0x3E:
                return Close();

            case 0x3F:
                return ReadFile();

            case 0x42:
                return Seek();

            case 0x48:
                return Allocate();

            case 0x49:
                return Free();

            case 0x4A:
                return Resize();

            case 0x2C: // get the time, which the game may use to seed itself
                _machine.Registers[Machine8086.CX] = 0;
                _machine.Registers[Machine8086.DX] = 0;
                return true;

            case 0x09:
                return PrintString();

            default:
                throw new NotSupportedException(
                    $"INT 21h AH={Ah:X2}h at {_machine.Segments[Machine8086.CS]:X4}:{_machine.Ip:X4} "
                    + "is not modelled - model the service rather than reporting success");
        }
    }

    private ushort _dtaSegment;
    private ushort _dtaOffset;

    private bool Open()
    {
        var name = StringAt(_machine.Segments[Machine8086.DS], Dx);
        Opened.Add(name);
        OpenedFrom.Add((_machine.Segments[Machine8086.DS], Dx));

        // DOS takes a path; the volume is flat, so only the last component
        // matters. The game opens by bare name anyway.
        var bare = name.Split('\\', '/')[^1];
        var path = Find(bare);

        if (path is null)
        {
            Fail(0x0002); // file not found
            return true;
        }

        var handle = _nextHandle++;
        _handles[handle] = new OpenFile(path, File.ReadAllBytes(path));
        Succeed(handle);
        return true;
    }

    private bool Close()
    {
        if (!_handles.Remove(Bx))
        {
            Fail(0x0006); // invalid handle
            return true;
        }

        Succeed();
        return true;
    }

    private bool ReadFile()
    {
        if (!_handles.TryGetValue(Bx, out var file))
        {
            Fail(0x0006);
            return true;
        }

        var wanted = Cx;
        // Position can now be past the end, because seek no longer clamps.
        var from = Math.Min(file.Position, file.Data.Length);
        var available = file.Data.Length - from;
        var count = Math.Min(wanted, available);

        // Byte at a time through the machine's own accessor, because a read
        // into the video aperture has to go through the card like any other
        // write. Nothing does that here, and relying on it not happening is
        // how it eventually does.
        for (var i = 0; i < count; i++)
        {
            _machine.WriteByte(
                _machine.Segments[Machine8086.DS],
                (ushort)(Dx + i),
                file.Data[from + i]);
        }

        file.Position += count;
        Succeed((ushort)count);
        return true;
    }

    private bool Seek()
    {
        if (!_handles.TryGetValue(Bx, out var file))
        {
            Fail(0x0006);
            return true;
        }

        var offset = Cx << 16 | Dx;
        var position = Al switch
        {
            0 => offset,
            1 => file.Position + offset,
            2 => file.Data.Length + offset,
            _ => throw new NotSupportedException($"INT 21h AH=42h with AL={Al:X2}h"),
        };

        // DOS does NOT clamp. Seeking past the end is legal and the reported
        // position is the one asked for - that is how a file gets extended by
        // writing there. Clamping reported 7D00h for a seek to 65,536 in a
        // 32,000-byte file: a plausible wrong answer, which is the one thing
        // this harness must not give.
        if (position < 0)
        {
            Fail(0x0019); // seek error
            return true;
        }

        file.Position = position;
        Ax = (ushort)file.Position;
        _machine.Registers[Machine8086.DX] = (ushort)(file.Position >> 16);
        _machine.Carry = false;
        return true;
    }

    private bool Allocate()
    {
        var paragraphs = Bx;
        var block = _arena;

        // ARITHMETIC IN INT, NOT USHORT. Adding a request of FFFFh to a ushort
        // arena wrapped, the check below passed, and the next allocation handed
        // out the same segment again.
        var next = block + paragraphs + 1;

        // A000 is the card. Handing out memory that overlaps it would mean the
        // program writing its own data through the graphics controller.
        //
        // And a REFUSED allocation must not move the arena. It did, so a
        // program that asked for too much and then asked for one paragraph was
        // refused the second time as well.
        if (next >= Machine8086.VideoSegment)
        {
            Fail(0x0008); // insufficient memory
            _machine.Registers[Machine8086.BX] = (ushort)(Machine8086.VideoSegment - block);
            return true;
        }

        _arena = (ushort)next;
        _blocks.Add(block);
        Succeed(block);
        return true;
    }

    /// <summary>
    /// Free a block, which this arena cannot actually reclaim.
    /// </summary>
    /// <remarks>
    /// The arena only goes up, so nothing is given back - but the segment is
    /// CHECKED. Reporting success for <c>ES=BEEFh</c> is the silent-success
    /// failure this file's own header says it does not have.
    /// </remarks>
    private bool Free()
    {
        if (!_blocks.Contains(_machine.Segments[Machine8086.ES]))
        {
            Fail(0x0009); // invalid memory block address
            return true;
        }

        Succeed();
        return true;
    }

    private bool Resize()
    {
        // The program shrinking its own allocation before asking for more,
        // which is the normal .EXE opening move. The PSP's own block is
        // resizable by definition; anything else has to be one we handed out,
        // for the same reason free checks.
        var block = _machine.Segments[Machine8086.ES];
        if (block != PspSegment && !_blocks.Contains(block))
        {
            Fail(0x0009);
            return true;
        }

        Succeed();
        return true;
    }

    private bool PrintString()
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < 256; i++)
        {
            var b = _machine.ReadByte(_machine.Segments[Machine8086.DS], (ushort)(Dx + i));
            if (b == '$') break;
            text.Append((char)b);
        }

        Printed.Add(text.ToString());
        return true;
    }

    /// <summary>Anything the program printed, which is usually an error.</summary>
    public List<string> Printed { get; } = [];

    /// <summary>Everything the BIOS teletype was asked to draw.</summary>
    public System.Text.StringBuilder Teletype { get; } = new();

    /// <summary>
    /// Keys waiting to be read, as the BIOS reports them: scancode then ASCII.
    /// </summary>
    /// <remarks>
    /// A script, not a keyboard. The whole run has to be reproducible, and a
    /// harness that waited for a real key could not be a test.
    /// </remarks>
    public Queue<ushort> Keys { get; } = new();

    /// <summary>How many times the program asked for a key that was not there.</summary>
    /// <remarks>
    /// The number that says a run has stalled. The first boot spent 2,499,361
    /// calls here and looked like it was running.
    /// </remarks>
    public long KeyWaits { get; private set; }

    /// <summary>Queue a key by its ASCII character, with no scancode.</summary>
    public void Type(string text)
    {
        foreach (var c in text)
        {
            Keys.Enqueue(c);
        }
    }

    private bool Keyboard()
    {
        switch (Ah)
        {
            case 0x00: // wait for a key and take it
            case 0x10:
                if (Keys.Count == 0)
                {
                    KeyWaits++;
                    Ax = 0;
                    return true;
                }

                Ax = Keys.Dequeue();
                return true;

            case 0x01: // is one waiting? zero flag set means no
            case 0x11:
                if (Keys.Count == 0)
                {
                    KeyWaits++;
                    _machine.Zero = true;
                    return true;
                }

                Ax = Keys.Peek();
                _machine.Zero = false;
                return true;

            case 0x02: // the shift state, which nothing is holding
            case 0x12:
                Ax = (ushort)(Ax & 0xFF00);
                return true;

            default:
                throw new NotSupportedException($"INT 16h AH={Ah:X2}h is not modelled");
        }
    }

    /// <summary>Video modes the program asked for, in order.</summary>
    public List<int> Modes { get; } = [];

    /// <summary>
    /// Interrupt vectors the program took over, in order.
    /// </summary>
    /// <remarks>
    /// The list that says how a run has to be driven. A game that hooks the
    /// timer is waiting for something to deliver IRQ 0, and a harness that
    /// never does will sit at a "press a key" screen looking busy.
    /// </remarks>
    public List<byte> Hooked { get; } = [];

    private bool Bios10()
    {
        switch (Ah)
        {
            case 0x00:
                Modes.Add(Al);
                if (_machine.Video is { } video)
                {
                    video.Clear();
                    video.Mode = Al;
                }

                return true;

            // Set the cursor shape, set its position, read it back. None of
            // the three touches a pixel in a graphics mode.
            case 0x01:
            case 0x02:
            case 0x03:
                return true;

            // Scroll, and write a character with an attribute. These DO change
            // the picture in a graphics mode, and returning quietly reported
            // success for work that was not done. Nothing in either build calls
            // them; if something does, the throw says so rather than leaving a
            // screen that is wrong in a way no comparison can explain.
            case 0x06:
            case 0x07:
            case 0x09:
            case 0x0A:
                throw new NotSupportedException(
                    $"INT 10h AH={Ah:X2}h changes the picture in a graphics mode "
                    + "and is not modelled - model it rather than reporting success");

            // Teletype. In a graphics mode the BIOS draws an 8x8 glyph from
            // the ROM font straight into the planes, so this DOES change the
            // picture - the first run painted 952 pixels this way and nothing
            // else.
            //
            // The characters are recorded, which is what a probe needs. The
            // glyphs are not drawn, which is a KNOWN GAP and the reason for
            // Teletype being public: any comparison of a screen that carries
            // BIOS text will differ by exactly the text, and it must be
            // recognised as this rather than investigated as a port fault.
            // Closing it needs the 8x8 ROM font, which is IBM's and is not in
            // this repository.
            case 0x0E:
                Teletype.Append((char)Al);
                return true;

            // Set the palette registers, which decide what the plane bits mean.
            // Ignoring these would leave two runs with identical pixels and
            // different pictures looking the same.
            case 0x10:
                return Palette();

            case 0x0F: // get the current mode
                Ax = (ushort)((_machine.Video?.Mode ?? 0) | 40 << 8);
                return true;

            // Alternate select - the call that only exists on an EGA, which is
            // exactly why a 1988 game makes it. This is the card detection,
            // and answering it wrongly is how a game decides it is talking to
            // a CGA and draws a different picture on purpose.
            // Display combination code - the call that only exists on a VGA,
            // and the VGA build's equivalent of the EGA build's AH=12h probe.
            // AL=1Ah says the call is supported; BL=08h is "VGA with an
            // analogue colour monitor".
            case 0x1A when Al == 0x00:
                Ax = 0x001A;
                _machine.Registers[Machine8086.BX] = 0x0008;
                return true;

            case 0x12 when (byte)Bx == 0x10:
                // BH is the colour/mono flag and BL is the memory size, so
                // this is 0003h and not 0300h. Written the wrong way round
                // first, and the game answered by setting mode 4 - CGA, four
                // colours - which is exactly the "decides it is talking to a
                // CGA" failure this call exists to prevent.
                _machine.Registers[Machine8086.BX] = 0x0003; // colour, 256 KB
                _machine.Registers[Machine8086.CX] = 0x0009; // no features, switches 9
                return true;

            default:
                throw new NotSupportedException(
                    $"INT 10h AH={Ah:X2}h at {_machine.Segments[Machine8086.CS]:X4}:{_machine.Ip:X4} "
                    + "is not modelled");
        }
    }

    private bool Palette()
    {
        if (_machine.Video is not { } video) return true;

        switch (Al)
        {
            case 0x00: // one register, BL, to the value in BH
                video.Attributes[Bx & 0x1F] = (byte)(Bx >> 8);
                return true;

            case 0x02: // all seventeen, from ES:DX
                for (var i = 0; i < 17; i++)
                {
                    video.Attributes[i] = _machine.ReadByte(
                        _machine.Segments[Machine8086.ES], (ushort)(Dx + i));
                }

                return true;

            case 0x01: // the overscan register
                return true;

            // The VGA colour registers. AL=10h sets one from DH/CH/CL;
            // AL=12h sets a block of CX of them from ES:DX, three bytes each.
            // Six bits a channel, which is what the DAC holds - a value above
            // 63 is the program's business, not ours to clamp.
            case 0x10:
                video.SetColour(Bx & 0xFF, (byte)(Dx >> 8), (byte)(Cx >> 8), (byte)Cx);
                return true;

            case 0x12:
                for (var i = 0; i < Cx; i++)
                {
                    video.SetColour(
                        Bx + i,
                        _machine.ReadByte(_machine.Segments[Machine8086.ES], (ushort)(Dx + i * 3)),
                        _machine.ReadByte(_machine.Segments[Machine8086.ES], (ushort)(Dx + i * 3 + 1)),
                        _machine.ReadByte(_machine.Segments[Machine8086.ES], (ushort)(Dx + i * 3 + 2)));
                }

                return true;

            default:
                throw new NotSupportedException($"INT 10h AH=10h AL={Al:X2}h is not modelled");
        }
    }
}
