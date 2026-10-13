namespace Rockford.Reference;

/// <summary>
/// Boots the original and runs it to a chosen instruction.
/// </summary>
/// <remarks>
/// <para>
/// Comparing a free-running emulator against the port cannot work: the original
/// loops, animating, and stops wherever the instruction budget runs out.
/// Stopping at a chosen address makes the comparison deterministic - run until
/// the cave loader returns and both sides are describing the same moment.
/// </para>
/// <para>
/// An address here is a file offset into the unpacked image, which is
/// how <c>docs/PORT-PLAN.md</c> and the 488 citations in
/// <c>src/Rockford.Engine</c> quote them. <see cref="Resolve"/> turns one into
/// the segment and offset it actually lives at once the program has unpacked
/// itself, so a number read off the port's comments can be used here without
/// being converted first.
/// </para>
/// </remarks>
public static class Harness
{
    /// <summary>
    /// Where the image sits, as a segment.
    /// </summary>
    /// <remarks>
    /// EXEPACK unpacks in place, so the program's own code segment is not the
    /// one DOS jumped to - but the unpacked image starts at the load segment
    /// all the same, which is what makes a <c>ds:</c> offset in
    /// <c>docs/PORT-PLAN.md</c> a file offset into <c>unpacked/vga.bin</c>.
    /// The code segment for anything at or above file <c>0x10000</c> is this
    /// plus <c>0x1000</c>.
    ///
    /// This doc comment used to claim a <c>Booted</c> member that recorded the
    /// base by finding it, "asserted by every test". No such member was ever
    /// written. It was a description of a design, presented as a description
    /// of the code.
    /// </remarks>
    public const ushort ImageSegment = Dos.LoadSegment;

    public sealed record Result(
        Machine8086 Machine,
        Dos Dos,
        Display Video,
        long Instructions,
        bool Reached,
        string? Fault);

    /// <summary>
    /// A machine with the game loaded and nothing run yet.
    /// </summary>
    public static (Machine8086 Machine, Dos Dos, Display Video) Boot(string exe, params string[] volume)
    {
        var machine = new Machine8086();
        var video = new Display { Machine = machine };
        var dos = new Dos(machine, volume);

        machine.Video = video;
        machine.Peripherals = video;

        dos.Load(File.ReadAllBytes(exe));
        return (machine, dos, video);
    }

    /// <summary>
    /// Boot from a directory holding an extracted disk.
    /// </summary>
    public static (Machine8086 Machine, Dos Dos, Display Video) BootFrom(params string[] volume) =>
        Boot(Path.Combine(volume[0], "ROCKFORD.EXE"), volume);

    /// <summary>
    /// Run until an address is reached, or the budget is spent.
    /// </summary>
    /// <param name="at">
    /// A linear address - segment times sixteen plus offset - because the
    /// program spans more than one segment and an offset alone is ambiguous.
    /// Pass null to run the whole budget.
    /// </param>
    public static Result Run(
        (Machine8086 Machine, Dos Dos, Display Video) machine,
        int? at = null,
        long limit = 50_000_000,
        Func<Machine8086, bool>? until = null,
        Dictionary<int, long>? histogram = null,
        Queue<(long Tick, byte Scancode)>? keys = null,
        int? observeAt = null,
        Action<Machine8086>? observe = null,
        bool timer = true)
    {
        var (m, dos, video) = machine;
        var interpreter = new Interpreter(m);
        string? fault = null;
        var reached = false;
        // Delivery bookkeeping lives on the machine now. As locals, two
        // one-instruction runs lost an interrupt that one two-instruction run
        // delivered, so where you paused changed the answer.

        try
        {
            for (var i = 0; i < limit && !m.Stopped; i++)
            {
                if (at is { } target
                    && Machine8086.Linear(m.Segments[Machine8086.CS], m.Ip) == target)
                {
                    reached = true;
                    break;
                }

                // A condition rather than an address, for the moments that are
                // easier to name by what has happened than by where the
                // instruction pointer is - "the second file has been opened"
                // needs no disassembly at all.
                if (until is not null && until(m))
                {
                    reached = true;
                    break;
                }

                // THE CONTROLLER, in its own priority order. IRQ 0 is a
                // higher priority line than IRQ 1 on an 8259 and is checked
                // first; the first version checked keys first, which let a
                // queue that was continually overdue starve the timer
                // completely - twenty keys delivered, fifty wraps, and not
                // one timer interrupt.
                //
                // Four conditions, all of them hardware: the line is not
                // masked at port 21h, interrupts are enabled, the
                // one-instruction shadow after `sti` or a load of SS has
                // passed, and no line of equal or higher priority is still in
                // service. That last one is per-line: an 8259 lets IRQ 0 nest
                // over a keyboard handler, and a single blanket flag stopped
                // the clock for the whole of one.
                var canInterrupt = m.InterruptsEnabled && !m.InterruptShadow;

                if (timer && canInterrupt
                    && m.LineEnabled(0)
                    && m.CanNest(0)
                    && m.Pit.Wraps != m.LastTimerWrap)
                {
                    m.LastTimerWrap = m.Pit.Wraps;
                    m.RaiseLine(0);
                }
                else if (canInterrupt
                         && m.CanNest(1)
                         && m.LineEnabled(1)
                         && keys is not null && keys.Count > 0
                         && m.Pit.Wraps >= keys.Peek().Tick
                         && m.Pit.Wraps != m.LastKeyWrap)
                {
                    // The game hooks INT 09h and reads port 60h itself, so a
                    // key has to arrive as an interrupt. The BIOS buffer only
                    // gets one as far as the "press a key to skip" poll.
                    //
                    // Scheduled against the clock rather than the instruction
                    // count, because that is the thing both sides of a
                    // comparison can agree on.
                    m.LastKeyWrap = m.Pit.Wraps;
                    m.Scancode = keys.Dequeue().Scancode;
                    m.RaiseLine(1);
                }

                // A watcher that does not stop the run. `until` answers "when
                // did this happen"; this answers "what did it do every time",
                // which is what comparing a loop against a reference decoder
                // needs.
                if (observeAt is { } watch
                    && Machine8086.Linear(m.Segments[Machine8086.CS], m.Ip) == watch)
                {
                    observe?.Invoke(m);
                }

                if (histogram is not null)
                {
                    var here = Machine8086.Linear(m.Segments[Machine8086.CS], m.Ip);
                    histogram[here] = histogram.GetValueOrDefault(here) + 1;
                }

                interpreter.Step();
            }
        }
        catch (Exception failed)
        {
            fault = $"{failed.Message} after {m.Executed:N0} instructions";
        }

        return new Result(m, dos, video, m.Executed, reached, fault);
    }
}
