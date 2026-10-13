namespace Rockford.Engine;

/// <summary>
/// Which of the driver's eight requests was served this frame, from the tests at
/// <c>0x133B5</c>. The values are the request bits themselves, so a front end can
/// switch on them directly.
///
/// The original had no use for this: it had one square wave and a pitch was the
/// whole of a sound's identity. It matters here because a second presentation
/// wants to play a designed sound per event, and recovering the event by matching
/// the pitch back would break the moment two of them chose the same divisor.
/// </summary>
[Flags]
public enum SoundEvent : byte
{
    /// <summary>Nothing playing.</summary>
    None = 0x00,

    /// <summary>
    /// Treasure landing or rolling. Two pitches, drawn per event, so a stream of
    /// falling gems does not sound mechanical.
    /// </summary>
    TreasureFalling = 0x01,

    /// <summary>
    /// Walking. Raised at <c>0x11F06</c> whenever the walk routine's tail finds the
    /// player standing somewhere new, so it is a footstep and it fires on every
    /// step taken.
    ///
    /// The two pitches are 52 and 58 hertz, which is not a note - on a PC speaker
    /// it is a rattle. Alone among the two-pitch handlers it alternates them with
    /// a toggle rather than a random draw.
    ///
    /// It was catalogued as belonging to an unported entity for a while, because
    /// nothing in the port raised it and the name describes the pitch rather than
    /// the cause.
    /// </summary>
    LowRumble = 0x02,

    /// <summary>Treasure taken, or a clock bonus picked up.</summary>
    Collect = 0x04,

    /// <summary>
    /// The fire button, and an explosion. One sound serves both in the original,
    /// which is deliberate rather than an economy: firing is an explosion.
    /// </summary>
    Blast = 0x08,

    /// <summary>A boulder landing or rolling. Two pitches, as with treasure.</summary>
    BoulderFalling = 0x10,

    /// <summary>The expanding wall growing.</summary>
    WallGrowing = 0x20,

    /// <summary>
    /// A held tone at a pitch the caller sets. Speaks on every eighth interrupt tick
    /// and never clears itself. Raised by the low-time alarm, at <c>0x13178</c>.
    /// </summary>
    VariableTone = 0x40,

    /// <summary>
    /// A held tick at a fixed pitch, every eighth interrupt. Not yet raised by
    /// anything ported.
    /// </summary>
    Tick = 0x80,
}

/// <summary>
/// One frame of the sound driver: what happened, and the pitch the hardware would
/// have been given for it.
/// </summary>
/// <param name="Event">Which request was served.</param>
/// <param name="Divisor">
/// The PIT channel 2 divisor. Zero means silence, which happens even for a live
/// event: the two holding tones speak only on every eighth tick and are silent
/// between.
/// </param>
public readonly record struct SoundFrame(SoundEvent Event, int Divisor)
{
    /// <summary>The tone in hertz, or zero for silence.</summary>
    public double Frequency => Simulation.FrequencyOf(Divisor);
}
