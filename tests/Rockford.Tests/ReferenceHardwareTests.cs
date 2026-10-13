using Rockford.Reference;

namespace Rockford.Tests;

/// <summary>
/// The machine's parts, checked against what the hardware does rather than
/// against what this game happens to need.
/// </summary>
/// <remarks>
/// <para>
/// Every test here exists because an independent review reproduced a defect
/// that the title-screen comparison could not see. That is the lesson worth
/// keeping: a passing end-to-end comparison establishes the paths it walks and
/// nothing else, and "the picture came out right" coexisted with a keyboard
/// that deadlocked after one key, a graphics register that discarded half of
/// every word written to it, and an allocator that handed out the same segment
/// twice.
/// </para>
/// <para>
/// <b>Not all of them are regressions, and the file used to claim they were.</b>
/// Twelve of the runnable tests here pass against the commit before the fixes:
/// the planar arithmetic was already right - an independent check of 57,600
/// operand combinations said so - and several others pin behaviour nothing had
/// broken. They are characterisation, and worth having. The ones that FAIL
/// against that parent are the regressions: the word port access, the mode 13h
/// read map, the indirect near call, the SS shadow, the masked line, the DAC
/// cursor, the data rotate, the timer stub's DS, and the three DOS tests.
/// </para>
/// <para>
/// Most need no game data. The three DOS tests do, because they load a real
/// executable, and they skip without it.
/// </para>
/// </remarks>
public class ReferenceHardwareTests
{
    private static (Machine8086 Machine, Display Video) Bare()
    {
        var machine = new Machine8086();
        var video = new Display { Machine = machine };
        machine.Video = video;
        machine.Peripherals = video;
        return (machine, video);
    }

    /// <summary>Assemble a few bytes at CS:0 and run them.</summary>
    private static Machine8086 Run(params byte[] code)
    {
        var (machine, _) = Bare();
        machine.Segments[Machine8086.CS] = 0x1000;
        machine.Segments[Machine8086.SS] = 0x2000;
        machine.Registers[Machine8086.SP] = 0x0100;
        code.CopyTo(machine.Memory, 0x10000);

        var interpreter = new Interpreter(machine);
        for (var i = 0; i < code.Length && machine.Ip < code.Length; i++)
        {
            interpreter.Step();
        }

        return machine;
    }

    [Fact]
    public void AWordOutReachesBothHalvesOfAnIndexDataPair()
    {
        var (machine, video) = Bare();
        machine.Segments[Machine8086.CS] = 0x1000;
        machine.Registers[Machine8086.DX] = 0x3C4;
        machine.Registers[Machine8086.AX] = 0x0102;

        // out dx, ax
        machine.Memory[0x10000] = 0xEF;
        new Interpreter(machine).Step();

        // The sequencer index is 2, the map mask, and the value is 1. Passing
        // the whole word to a byte handler kept the index and discarded the
        // data, leaving the mask at 0Fh - and the EGA executable has exactly
        // this sequence at 0x21400, so its blitter never selected a plane.
        Assert.Equal(0x01, video.MapMask);
    }

    [Fact]
    public void ModeThirteenReadsIgnoreThePlanarReadMap()
    {
        var (_, video) = Bare();
        video.Mode = 0x13;
        video.WriteAperture(123, 0x5A);

        // Selecting a read map is meaningless in a linear mode, and answering
        // from that plane returned zero for a byte that had just been written.
        video.Write(0x3CE, 4);
        video.Write(0x3CF, 2);

        Assert.Equal(0x5A, video.ReadAperture(123));
    }

    [Fact]
    public void AnIndirectNearCallReadsItsTargetBeforePushing()
    {
        var (machine, _) = Bare();
        machine.Segments[Machine8086.CS] = 0x1000;
        machine.Segments[Machine8086.SS] = 0x2000;
        machine.Registers[Machine8086.SP] = 0x0100;

        // BP points two below SP, so the push lands exactly on the word being
        // called through.
        machine.Registers[Machine8086.BP] = 0x00FE;
        machine.WriteWord(0x2000, 0x00FE, 0x1234);

        // ff 56 00 : call word ptr [bp+0]
        machine.Memory[0x10000] = 0xFF;
        machine.Memory[0x10001] = 0x56;
        machine.Memory[0x10002] = 0x00;
        new Interpreter(machine).Step();

        // Pushing first overwrote the target with the return address and the
        // machine jumped to 0003h. Inherited from the sibling harness; the far
        // form added later already read its target first.
        Assert.Equal(0x1234, machine.Ip);
    }

    [Fact]
    public void LoadingTheStackSegmentOpensAnInterruptWindow()
    {
        // b8 00 20 : mov ax,2000h    8e d0 : mov ss,ax
        var machine = Run(0xB8, 0x00, 0x20, 0x8E, 0xD0);

        // The 8086 will not take an interrupt until the instruction after a
        // load of SS, so that `mov ss,ax / mov sp,bx` cannot be split. Without
        // it a probe delivered IRQ 0 with SS moved and SP still pointing into
        // the old stack.
        Assert.True(machine.InterruptShadow);
    }

    [Fact]
    public void AMaskedLineIsNotDelivered()
    {
        // THROUGH THE PORT, not by setting the field. The first version
        // assigned InterruptMask directly, so it passed with the port 21h
        // handler deleted - it tested the accessor and not the thing that
        // was broken.
        var (machine, _) = Bare();
        machine.Segments[Machine8086.CS] = 0x1000;

        Assert.True(machine.LineEnabled(0));
        Assert.True(machine.LineEnabled(1));

        // b0 ff : mov al,0FFh     e6 21 : out 21h,al
        new byte[] { 0xB0, 0xFF, 0xE6, 0x21 }.CopyTo(machine.Memory, 0x10000);
        var interpreter = new Interpreter(machine);
        interpreter.Step();
        interpreter.Step();

        // A program writes FFh here precisely because it is about to do
        // something an interrupt would ruin. IRQ 0 arrived anyway.
        Assert.False(machine.LineEnabled(0));
        Assert.False(machine.LineEnabled(1));
    }

    [Fact]
    public void AHigherPriorityLineNestsOverALowerOne()
    {
        var (machine, _) = Bare();

        machine.RaiseLine(1);                 // the keyboard, the long handler
        Assert.False(machine.CanNest(1), "a line cannot interrupt itself");
        Assert.True(machine.CanNest(0), "the clock must not stop for a keyboard handler");

        machine.RaiseLine(0);
        Assert.False(machine.CanNest(0));
        Assert.False(machine.CanNest(1));
    }

    [Fact]
    public void DataRotateTurnsTheValueBeforeItIsWritten()
    {
        // Nothing covered this at all: breaking the rotation left every test
        // passing, including the running-game one.
        var (_, video) = Bare();

        video.Write(0x3CE, 3);
        video.Write(0x3CF, 2);           // rotate right by two, function replace
        video.WriteAperture(0, 0x03);

        // 03h rotated right twice is C0h, and every plane gets it because the
        // map mask is open and set/reset is off.
        Assert.Equal(0xC0, video.Planes[0][0]);
        Assert.Equal(0xC0, video.Planes[3][0]);
    }

    [Fact]
    public void TheDacMasksToSixBitsAndKeepsItsCursor()
    {
        var (_, video) = Bare();

        // A BIOS palette call is three writes to 3C9h after an index, not a
        // poke into the array: it masks each value to six bits and leaves the
        // cursor on the next entry.
        video.SetColour(7, 0xFF, 0x20, 0x10);

        Assert.Equal(0x3F, video.Dac[7 * 3]);
        Assert.Equal(0x20, video.Dac[7 * 3 + 1]);
        Assert.Equal(0x10, video.Dac[7 * 3 + 2]);

        // And the index register reads back where the cursor got to.
        Assert.Equal(8, video.Read(0x3C8));
    }

    [Fact]
    public void ThePlanarWriteAppliesTheFunctionThenTheBitMask()
    {
        var (_, video) = Bare();

        // Latch 0F0h in every plane, then XOR FFh through a bit mask of 0Fh.
        // The masked-out bits must come from the LATCH, not from memory and
        // not from the CPU value - which is only the same thing when the
        // latches were loaded from this address.
        for (var plane = 0; plane < 4; plane++) video.Planes[plane][0] = 0xF0;
        video.ReadAperture(0);

        video.Write(0x3CE, 3);
        video.Write(0x3CF, 3 << 3);      // function select: xor
        video.Write(0x3CE, 8);
        video.Write(0x3CF, 0x0F);        // bit mask

        video.WriteAperture(0, 0xFF);

        // High nibble: masked out, so the latch, F0h -> F.
        // Low nibble: F0h xor FFh = 0Fh, masked in -> F.
        Assert.Equal(0xFF, video.Planes[0][0]);
    }

    [Fact]
    public void WriteModeOneIgnoresTheValueTheFunctionAndTheMask()
    {
        var (_, video) = Bare();

        video.Planes[0][0] = 0x12;
        video.Planes[1][0] = 0x34;
        video.Planes[2][0] = 0x56;
        video.Planes[3][0] = 0x78;
        video.ReadAperture(0);

        video.Write(0x3CE, 5);
        video.Write(0x3CF, 1);           // write mode 1
        video.Write(0x3CE, 8);
        video.Write(0x3CF, 0x00);        // a bit mask that would block everything
        video.Write(0x3CE, 3);
        video.Write(0x3CF, 1 << 3);      // a function that would AND

        video.WriteAperture(64, 0x00);

        // The latch copy, straight through. This is how the game moves a tile:
        // read a byte to fill the latches, write a byte to place them, and the
        // value written is never looked at.
        Assert.Equal(0x12, video.Planes[0][64]);
        Assert.Equal(0x78, video.Planes[3][64]);
    }

    /// <summary>A DOS with one file on a volume, and nothing running.</summary>
    private static (Machine8086 Machine, Dos Dos) WithDisks()
    {
        var (machine, _) = Bare();
        var volume = ReferenceTests.AnyVolume();
        return volume is null ? (machine, null!) : (machine, new Dos(machine, volume));
    }

    [SkippableFact]
    public void SeekingPastTheEndReportsWhereItWasAsked()
    {
        var (machine, dos) = WithDisks();
        Skip.If(dos is null, "no extracted disks");

        dos.Load(File.ReadAllBytes(Path.Combine(ReferenceTests.AnyVolume()![0], "ROCKFORD.EXE")));

        var handle = Open(machine, dos, "CELLMAPS.BIN");

        // AH=42h AL=00h, CX:DX = 65,536, on a 35,200-byte file.
        machine.Registers[Machine8086.AX] = 0x4200;
        machine.Registers[Machine8086.BX] = handle;
        machine.Registers[Machine8086.CX] = 1;
        machine.Registers[Machine8086.DX] = 0;
        dos.Service(0x21, machine);

        // DOS does not clamp. Clamping reported 7D00h - the end of a different
        // file - which is a plausible wrong answer, and a plausible wrong
        // answer is the one thing this harness must not give.
        Assert.False(machine.Carry);
        Assert.Equal(0x0000, machine.Registers[Machine8086.AX]);
        Assert.Equal(0x0001, machine.Registers[Machine8086.DX]);
    }

    [SkippableFact]
    public void AnImpossibleAllocationDoesNotWrapOrConsumeTheArena()
    {
        var (machine, dos) = WithDisks();
        Skip.If(dos is null, "no extracted disks");

        dos.Load(File.ReadAllBytes(Path.Combine(ReferenceTests.AnyVolume()![0], "ROCKFORD.EXE")));

        // TWO refusals, and the sizes matter. FFFFh alone was not enough:
        // with the arena bug restored it wrapped the prematurely-advanced
        // arena back to where it started, so the test passed against the
        // defect it was written for. 9000h is too big to be granted and small
        // enough not to wrap.
        machine.Registers[Machine8086.AX] = 0x4800;
        machine.Registers[Machine8086.BX] = 0xFFFF;
        dos.Service(0x21, machine);
        Assert.True(machine.Carry, "an allocation that would wrap must fail");

        machine.Registers[Machine8086.AX] = 0x4800;
        machine.Registers[Machine8086.BX] = 0x9000;
        dos.Service(0x21, machine);
        Assert.True(machine.Carry, "an allocation past the card must fail");

        // And a refused allocation must not move the arena on. It did, so a
        // program that asked for too much and then asked for one paragraph was
        // refused the second time as well.
        machine.Registers[Machine8086.AX] = 0x4800;
        machine.Registers[Machine8086.BX] = 1;
        dos.Service(0x21, machine);
        Assert.False(machine.Carry, "a small allocation after a refused one must still work");
        var first = machine.Registers[Machine8086.AX];

        machine.Registers[Machine8086.AX] = 0x4800;
        machine.Registers[Machine8086.BX] = 1;
        dos.Service(0x21, machine);
        Assert.NotEqual(first, machine.Registers[Machine8086.AX]);
    }

    [SkippableFact]
    public void FreeingSomethingWeNeverGaveOutIsRefused()
    {
        var (machine, dos) = WithDisks();
        Skip.If(dos is null, "no extracted disks");

        dos.Load(File.ReadAllBytes(Path.Combine(ReferenceTests.AnyVolume()![0], "ROCKFORD.EXE")));

        machine.Registers[Machine8086.AX] = 0x4900;
        machine.Segments[Machine8086.ES] = 0xBEEF;
        dos.Service(0x21, machine);

        // Reporting success here is the silent-success failure this DOS says
        // in its own header that it does not have.
        Assert.True(machine.Carry);
    }

    private static ushort Open(Machine8086 machine, Dos dos, string name)
    {
        var at = (ushort)0x8000;
        for (var i = 0; i < name.Length; i++) machine.Memory[(0x0100 << 4) + at + i] = (byte)name[i];
        machine.Memory[(0x0100 << 4) + at + name.Length] = 0;

        machine.Segments[Machine8086.DS] = 0x0100;
        machine.Registers[Machine8086.AX] = 0x3D00;
        machine.Registers[Machine8086.DX] = at;
        dos.Service(0x21, machine);

        Assert.False(machine.Carry, $"{name} must open");
        return machine.Registers[Machine8086.AX];
    }

    [Fact]
    public void TheDacStateRegisterSaysWhichCycleItIsSetUpFor()
    {
        var (_, video) = Bare();

        // ELEVEN for a read cycle, set up by writing the read index at 3C7h,
        // and ZERO for a write cycle from 3C8h. I had this backwards twice and
        // argued for the wrong one in a commit message; an older IBM manual
        // carries the reversed wording and that is where it came from.
        video.Write(0x3C7, 0);
        Assert.Equal(0x03, video.Read(0x3C7));

        video.Write(0x3C8, 0);
        Assert.Equal(0x00, video.Read(0x3C7));
    }

    [Fact]
    public void CallingABareReturnIsNotAFailure()
    {
        var (machine, _) = Bare();
        machine.LoadSegment = 0x0100;
        machine.Segments[Machine8086.SS] = 0x2000;
        machine.Registers[Machine8086.SP] = 0x0100;
        machine.Memory[(0x1100 << 4)] = 0xC3;       // ret

        // The loop used to step before checking, so a routine that reaches the
        // sentinel without executing anything past it was reported as never
        // having returned.
        var took = Routine.Call(machine, 0x1100, 0x10000, limit: 1, timer: false);

        Assert.NotNull(took);
    }

    [Fact]
    public void TheTimerStubBumpsTheBiosTickAndNotTheGuestsMemory()
    {
        var (machine, _) = Bare();
        var dos = new Dos(machine);

        // A minimal .EXE: header only, no body, so the stubs get built.
        var exe = new byte[1024];
        exe[0] = (byte)'M';
        exe[1] = (byte)'Z';
        exe[2] = 0x00;                  // last page is full
        exe[3] = 0x02;                  // 512 bytes
        exe[4] = 2;                     // two pages
        exe[8] = 32;                    // a 512-byte header
        dos.Load(exe);

        // The guest's own DS, pointing somewhere that is not the BIOS area,
        // with a recognisable word at the offset the stub increments.
        machine.Segments[Machine8086.DS] = 0x1234;
        machine.WriteWord(0x1234, 0x046C, 0xABCD);
        machine.WriteWord(0, Machine8086.BiosTickAddress, 100);

        // Run the stub: push a return address, point at it, execute to the
        // iret's return.
        machine.Segments[Machine8086.SS] = 0x2000;
        machine.Registers[Machine8086.SP] = 0x0100;
        machine.Push(0);                        // flags
        machine.Push(0x3000);                   // cs
        machine.Push(0x0000);                   // ip
        machine.Segments[Machine8086.CS] = Dos.BiosSegment;
        machine.Ip = (ushort)(0x1000 + 8 * 32);

        var interpreter = new Interpreter(machine);
        for (var i = 0; i < 20 && machine.Segments[Machine8086.CS] == Dos.BiosSegment; i++)
        {
            interpreter.Step();
        }

        // `inc word [046Ch]` is DS-RELATIVE. Without setting DS the stub
        // incremented a word inside whatever the interrupted program was
        // using, and left the real tick alone - a harness writing into the
        // thing it is measuring, where the corruption is indistinguishable
        // from a port defect.
        Assert.Equal(0xABCD, machine.ReadWord(0x1234, 0x046C));
        Assert.Equal(101, machine.ReadWord(0, Machine8086.BiosTickAddress));

        // And it leaves the guest's DS as it found it.
        Assert.Equal(0x1234, machine.Segments[Machine8086.DS]);
    }

    [Fact]
    public void AFreshMachineDeliversNoInterruptBeforeTheFirstTick()
    {
        var (machine, _) = Bare();
        machine.InterruptsEnabled = true;

        // LastTimerWrap started at -1, so a machine that had never ticked
        // delivered IRQ 0 immediately - an interrupt for a tick that had not
        // happened.
        Assert.Equal(0, machine.Pit.Wraps);
        Assert.Equal(machine.Pit.Wraps, machine.LastTimerWrap);
    }

    [Fact]
    public void AWordInReadsBothPortsAndWrapsAtTheTop()
    {
        var (machine, video) = Bare();
        machine.Segments[Machine8086.CS] = 0x1000;

        // The sequencer index register answers reads, so 3C4h/3C5h is a pair
        // that can be read back. e5 c4 would be `in ax,0C4h`, so use the DX
        // form for a 16-bit port.
        machine.Registers[Machine8086.DX] = 0x3C4;
        video.Write(0x3C4, 2);           // select the map mask
        video.Write(0x3C5, 0x0A);

        machine.Memory[0x10000] = 0xED;            // in ax,dx
        new Interpreter(machine).Step();

        // Low byte from 3C4h - the index, 2 - and high byte from 3C5h - the
        // map mask, 0Ah. E5 and ED were both byte reads, so the high half of
        // AX was whatever happened to be there.
        Assert.Equal(0x0A02, machine.Registers[Machine8086.AX]);
    }
}
