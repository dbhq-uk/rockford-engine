namespace Rockford.Reference;

/// <summary>
/// Calls one of the original's routines and hands back what it changed.
/// </summary>
/// <remarks>
/// <para>
/// This is what turns a running game into a comparison. A routine cited in
/// <c>docs/PORT-PLAN.md</c> is a file offset into the unpacked image; this
/// resolves it to the segment and offset the program actually reaches it at,
/// calls it with a return address that is not real code, and runs until
/// control comes back there.
/// </para>
/// <para>
/// The sentinel is the trick. A near <c>ret</c> pops an offset within the
/// current code segment, so the "address" to return to has to be one - and it
/// has to be somewhere the program will never execute, or the run stops in the
/// wrong place and looks like success. <see cref="Sentinel"/> is past the end
/// of the loaded image in that segment, which nothing can reach.
/// </para>
/// </remarks>
public static class Routine
{
    /// <summary>An offset in the code segment that is past the loaded image.</summary>
    /// <remarks>
    /// A near <c>ret</c> pops an offset within the current code segment, so
    /// the address to return to has to be one - and it has to be somewhere the
    /// program will never execute, or the run stops in the wrong place and
    /// looks like success. This is past the end of the image in that segment.
    /// </remarks>
    public const ushort Sentinel = 0xFFFE;

    /// <summary>
    /// The segment and offset a cited file offset is reached at.
    /// </summary>
    /// <remarks>
    /// <b>The code segment is read off the running program, not assumed.</b>
    /// EXEPACK unpacks each build to a different base - 1CFBh in the EGA one
    /// and 0E9Fh in the VGA one - and a hard-coded paragraph produced the
    /// right LINEAR address for the cave loader by coincidence while giving it
    /// the wrong CS. That is invisible until a routine reads
    /// <c>cs:</c>-relative data, and the frame pacer at <c>0x1334F</c> does
    /// exactly that: <c>mov ax,cs:[2]</c>, the tick counter it spins on.
    /// </remarks>
    public static (ushort Segment, ushort Offset) Resolve(
        ushort loadSegment, ushort codeSegment, int fileOffset)
    {
        var codeBase = (codeSegment - loadSegment) << 4;

        return fileOffset >= codeBase
            ? (codeSegment, (ushort)(fileOffset - codeBase))
            : (loadSegment, (ushort)fileOffset);
    }

    /// <summary>
    /// Call a near routine and run until it returns.
    /// </summary>
    /// <remarks>
    /// <b>The clock runs while it does.</b> The first version stepped
    /// instructions and delivered nothing, which is fine for a routine that
    /// only computes - the cave loader never looks at the time - and useless
    /// for one that waits. Calling the VGA frame pacer that way ran 100,000
    /// instructions, delivered no IRQ 0 and returned null, because the counter
    /// it spins on is incremented by the timer handler.
    ///
    /// A primitive that silently cannot call half the routines it is aimed at
    /// is worse than one that says so, so it delivers IRQ 0 on the same terms
    /// the run loop does.
    /// </remarks>
    /// <returns>How many instructions it took, or null if it never returned.</returns>
    public static long? Call(
        Machine8086 machine, ushort codeSegment, int fileOffset,
        long limit = 20_000_000, bool timer = true)
    {
        var (segment, offset) = Resolve(machine.LoadSegment, codeSegment, fileOffset);

        machine.Segments[Machine8086.CS] = segment;
        machine.Ip = offset;
        machine.Push(Sentinel);

        var interpreter = new Interpreter(machine);
        var before = machine.Executed;

        // BEFORE the first step, not after it. A routine that is a bare `ret`
        // reaches the sentinel without executing anything past it, and a loop
        // that stepped first reported it as never having returned.
        for (var i = 0L; i <= limit; i++)
        {
            if (machine.Ip == Sentinel && machine.Segments[Machine8086.CS] == segment)
            {
                return machine.Executed - before;
            }

            if (machine.Stopped) return null;

            if (timer
                && machine.Pit.Wraps != machine.LastTimerWrap
                && machine.InterruptsEnabled
                && !machine.InterruptShadow
                && machine.LineEnabled(0)
                && machine.CanNest(0))
            {
                machine.LastTimerWrap = machine.Pit.Wraps;
                machine.RaiseLine(0);
            }

            interpreter.Step();
        }

        return null;
    }
}
