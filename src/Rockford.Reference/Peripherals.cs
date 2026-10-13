namespace Rockford.Reference;

/// <summary>
/// The devices that are the machine's own, and the ones that are not.
/// </summary>
/// <remarks>
/// <para>
/// The interpreter came from the Alley Cat harness, where the whole machine was
/// a PC with a CGA and no operating system, so the ports and the BIOS calls
/// could live in the interpreter's own switch statements. Rockford is a DOS
/// program on an EGA, and none of that half is the same.
/// </para>
/// <para>
/// So the split here is between what an 8086 always has - the 8253, the 8259
/// and the keyboard controller - and what a particular machine is wearing. The
/// first stays in the interpreter because it is the same everywhere; the second
/// comes through here.
/// </para>
/// <para>
/// <b>The default throws, and that is the policy, not an omission.</b> An
/// unmodelled device that answers zero is how a comparison harness produces a
/// frame that looks almost right, and almost right is the one outcome worse
/// than a crash.
/// </para>
/// </remarks>
public interface IPeripherals
{
    /// <summary>Answer a port read, or throw if the device is not modelled.</summary>
    byte Read(int port);

    /// <summary>Take a port write, or throw if the device is not modelled.</summary>
    void Write(int port, int value);
}

/// <summary>
/// The software interrupts a machine answers itself: the BIOS, and DOS.
/// </summary>
/// <remarks>
/// Only the ones the program has <i>not</i> hooked reach here. The interpreter
/// checks the vector first and runs the program's own handler when it points
/// into the loaded image, because a hooked interrupt is the thing under test
/// and not ours to answer. That test is what made the Alley Cat keyboard path
/// work, and it matters more here: Rockford hooks the timer and the keyboard
/// and then calls INT 21h through the vectors DOS installed.
/// </remarks>
public interface IServices
{
    /// <summary>
    /// Answer an interrupt. Return false to let it fall through as a no-op.
    /// </summary>
    bool Service(byte number, Machine8086 machine);
}

/// <summary>The machine with nothing plugged into it.</summary>
public sealed class NoPeripherals : IPeripherals
{
    public static readonly NoPeripherals Instance = new();

    public byte Read(int port) => throw new NotSupportedException(
        $"read of port {port:X3}h is not modelled - model the device rather than answering zero");

    public void Write(int port, int value) => throw new NotSupportedException(
        $"write of {value:X2} to port {port:X3}h is not modelled");
}

/// <summary>A machine with no BIOS and no DOS: every service falls through.</summary>
public sealed class NoServices : IServices
{
    public static readonly NoServices Instance = new();

    public bool Service(byte number, Machine8086 machine) => false;
}
