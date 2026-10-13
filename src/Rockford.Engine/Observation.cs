namespace Rockford.Engine;

/// <summary>What the simulation is reporting.</summary>
public enum ObservationKind : byte
{
    /// <summary>
    /// A cell's contents moved from <c>From</c> to <c>To</c>. Both are cell
    /// addresses in the original's own terms, so <see cref="CaveGrid.ColumnOf"/>
    /// and <see cref="CaveGrid.RowOf"/> turn them into a column and a row.
    ///
    /// Reported only when the move has ALREADY happened. There is no "about to".
    /// </summary>
    Moved = 1,

    /// <summary>
    /// A falling object came to rest at <c>To</c>. <c>From</c> is the same cell:
    /// landing is not a move.
    ///
    /// This is what a contact effect wants, and it is the one thing the sound
    /// stream genuinely cannot give, because a sound frame carries no location.
    /// </summary>
    Landed = 2,

    /// <summary>
    /// An explosion, with <c>To</c> the centre cell and <c>Value</c> the kind -
    /// 1 leaves treasure behind, 2 leaves space.
    ///
    /// Note the centre, not the upper-left corner the handler is given. The
    /// renderer wants to know where the bang was.
    /// </summary>
    Blasted = 3,

    /// <summary>
    /// The player fired, at the cell in <c>To</c>.
    ///
    /// This exists because <see cref="SoundEvent.Blast"/> cannot tell firing from
    /// an explosion - one request serves both in the original, deliberately, and
    /// a front end that shook the screen on every Blast would shake it on every
    /// shot.
    /// </summary>
    Fired = 4,
}

/// <summary>
/// One thing the simulation did, reported after the fact.
///
/// <para>
/// This is a read-only window, not a hook. The engine records facts it has
/// already decided; the presentation chooses duration, particles, sound and
/// appearance from them. Nothing here is called back into, nothing here draws
/// from the random generator, and removing every <c>Observe</c> call would leave
/// the simulation running exactly as it does now. <c>FingerprintTests</c> is what
/// holds that true rather than the claim above.
/// </para>
///
/// <para>
/// It exists because consecutive snapshots of the grid cannot always tell the
/// truth. Two identical boulders side by side, one of which moved, produce the
/// same pair of before-and-after pictures as the other one moving - and
/// movement and replacement are indistinguishable. Where the association is
/// ambiguous a renderer must snap the sprite rather than invent a trajectory,
/// so what cannot be inferred has to be said.
/// </para>
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="From">The cell it started in, or the same as <paramref name="To"/> when nothing moved.</param>
/// <param name="To">The cell it ended in, or where the event happened.</param>
/// <param name="Value">
/// The runtime value involved, or the blast kind for <see cref="ObservationKind.Blasted"/>.
/// </param>
public readonly record struct Observation(ObservationKind Kind, int From, int To, byte Value)
{
    public static Observation Moved(int from, int to, byte value) =>
        new(ObservationKind.Moved, from, to, value);

    public static Observation Landed(int at, byte value) =>
        new(ObservationKind.Landed, at, at, value);

    public static Observation Blasted(int centre, int kind) =>
        new(ObservationKind.Blasted, centre, centre, (byte)kind);

    public static Observation Fired(int at) =>
        new(ObservationKind.Fired, at, at, 0);

    /// <summary>The column and row it started in.</summary>
    public (int Column, int Row) FromCell => (CaveGrid.ColumnOf(From), CaveGrid.RowOf(From));

    /// <summary>The column and row it ended in.</summary>
    public (int Column, int Row) ToCell => (CaveGrid.ColumnOf(To), CaveGrid.RowOf(To));
}
