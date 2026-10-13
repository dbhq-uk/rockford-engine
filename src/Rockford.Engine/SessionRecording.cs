namespace Rockford.Engine;

/// <summary>
/// Everything that was asked of a <see cref="GameHost"/>, in order.
/// </summary>
/// <remarks>
/// WHY A WHOLE SESSION AND NOT A CAVE.
///
/// "Cave number plus seed" is not enough to reproduce a run and never was. The
/// worms carry their headings between caves, the footstep state and the timed
/// wall's timestamp carry too - see <c>Simulation.CarryGlobalsFrom</c> - and
/// the score and lives are session state. Reproducing the fifth cave therefore
/// means reproducing the four before it, so the unit that can be replayed is
/// the session.
///
/// ON THE RANDOM STREAM, because it is easy to state this wrongly and an
/// earlier version of this comment did. THE ORIGINAL carries one continuing
/// stream across a session. THIS PORT does not: <c>GameHost.Load</c> builds
/// every cave's simulation with a fixed <c>0x04D2</c>, and the seed is not one
/// of the globals carried across. That is a known, deliberate difference and it
/// is written up in <c>docs/CICODIS-FEASIBILITY.md</c>, because it is the first
/// thing a comparison against a running original would trip over. It does not
/// weaken the case for recording whole sessions - the other carried globals
/// make that case on their own - and it is not this file's to change.
///
/// WHAT IS RECORDED.
///
/// The host's mutating surface, which is eight methods, and nothing else. Not
/// the resulting state, not the effects, not a frame of anything - if the
/// engine is deterministic then the inputs are sufficient, and if they are not
/// sufficient then recording the outputs would hide exactly the defect worth
/// finding. A replay that diverges is the interesting result.
///
/// WHAT IS NOT IN HERE.
///
/// Anything about presentation: which build, which atlas, what was drawn, what
/// was heard. A session recorded in VIP replays in the faithful build and must
/// give the same answer, because the simulation is the same and the difference
/// is paint.
/// </remarks>
public sealed record SessionRecording(SessionIdentity Identity, IReadOnlyList<HostCall> Calls);

/// <summary>
/// What this session was played against.
/// </summary>
/// <param name="CellmapsHash">The twenty caves, as authored.</param>
/// <param name="TablesHash">The per-cave quotas, time limits and score tables.</param>
/// <param name="WorldsHash">The world list: which cave record each one maps to.</param>
/// <param name="Engine">The engine assembly's version.</param>
/// <remarks>
/// A recording replayed against different data is not a replay, it is a
/// coincidence - and the failure would look like an engine divergence rather
/// than like the wrong file, which is the worst way for this to go wrong. The
/// hashes are checked before a single call is applied.
///
/// Hashes of the data rather than a path or a filename, because the question is
/// whether the BYTES are the same. Two checkouts disagreeing about
/// `cellmaps.bin` is precisely the case this has to catch.
/// </remarks>
public readonly record struct SessionIdentity(
    string CellmapsHash,
    string TablesHash,
    string WorldsHash,
    string Engine)
{
    /// <summary>The identity of the data a host was built on.</summary>
    public static SessionIdentity Of(byte[] cellmaps, CaveTables tables, WorldEntry[] worlds)
    {
        ArgumentNullException.ThrowIfNull(cellmaps);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(worlds);

        return new SessionIdentity(
            Hash(cellmaps),
            Hash(TableBytes(tables)),
            Hash(WorldBytes(worlds)),
            typeof(GameHost).Assembly.GetName().Version?.ToString() ?? "dev");
    }

    /// <summary>The world-list part of an identity on its own.</summary>
    public static string WorldsHashOf(WorldEntry[] worlds)
    {
        ArgumentNullException.ThrowIfNull(worlds);
        return Hash(WorldBytes(worlds));
    }

    /// <summary>
    /// FNV-1a, the same function the fingerprint tests use.
    /// </summary>
    /// <remarks>
    /// Not a cryptographic hash and does not need to be: nobody is attacking a
    /// replay file, and the question is only whether two byte arrays are the
    /// same one. Using the function already in the repository means one
    /// implementation to be wrong rather than two.
    /// </remarks>
    private static string Hash(ReadOnlySpan<byte> bytes)
    {
        ulong h = 14695981039346656037;
        foreach (var b in bytes)
        {
            h ^= b;
            h *= 1099511628211;
        }
        return h.ToString("x16");
    }

    /// <summary>
    /// The cave tables, flattened, so they can be hashed like the cellmaps.
    /// </summary>
    /// <remarks>
    /// Every field, not a sample. A table read out of a different executable
    /// build differs in the quotas and the time limits, and those are exactly
    /// the numbers a replay depends on.
    /// </remarks>
    /// <summary>
    /// The world list, flattened.
    /// </summary>
    /// <remarks>
    /// This was the COUNT, and the count was never even compared. Reorder
    /// worlds.json, or change one entry's raw World number while leaving the
    /// caves and the tables alone, and a replay would be accepted and then
    /// load a different cave record for the same call - a divergence with
    /// nothing to explain it. Astra, 19 September 2026.
    ///
    /// The raw World number is what Load uses to index the cave records, so it
    /// has to be in here. The sheet name is presentation and is in here anyway,
    /// because a list whose entries have been renamed is a list somebody
    /// changed, and it costs nothing to say so.
    /// </remarks>
    private static byte[] WorldBytes(WorldEntry[] worlds)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(worlds.Length));
        foreach (var world in worlds)
        {
            bytes.AddRange(BitConverter.GetBytes(world.World));
            bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(world.Sheet));
            bytes.Add(0);
        }
        return [.. bytes];
    }

    private static byte[] TableBytes(CaveTables tables)
    {
        var bytes = new List<byte>();
        foreach (var table in new[]
                 {
                     tables.Required, tables.Time, tables.ScoreBefore,
                     tables.ScoreAfter, tables.AmoebaCap,
                 })
        {
            // The length as well as the contents. Two tables of different
            // lengths can otherwise hash the same by concatenation, and a
            // truncated table is exactly the sort of half-read file this is
            // here to notice.
            bytes.AddRange(BitConverter.GetBytes(table.Length));
            foreach (var value in table) bytes.AddRange(BitConverter.GetBytes(value));
        }
        return [.. bytes];
    }
}

/// <summary>What kind of thing was asked of the host.</summary>
public enum HostCallKind
{
    /// <summary>One simulation step, with the action supplied to it.</summary>
    Tick,

    /// <summary>Back to the world chooser from the title.</summary>
    ToMenu,

    /// <summary>The chooser's highlight moved.</summary>
    MoveSelection,

    /// <summary>The highlighted world was started.</summary>
    StartSelectedWorld,

    /// <summary>A specific cave was loaded, which only the CLI and tests do.</summary>
    Load,

    /// <summary>The cave stepper, which is a debug affordance.</summary>
    Go,

    /// <summary>The player restarted the cave, spending a life.</summary>
    RestartCave,

    /// <summary>The player abandoned the run for the chooser.</summary>
    AbandonToChooser,

    /// <summary>The screen was set directly rather than by a transition.</summary>
    SetScreen,

    /// <summary>The chooser's highlight was set directly.</summary>
    SetSelected,
}

/// <summary>
/// One call, with whatever it carried.
/// </summary>
/// <param name="Kind">Which method.</param>
/// <param name="A">Its first argument, or 0. The packed action for a tick.</param>
/// <param name="B">Its second argument, or 0.</param>
/// <remarks>
/// Two integers rather than a field per method. The arguments are all small
/// integers already - an action is a bit set, a world and a stage are indices -
/// and a shape per call kind would be eight record types to serialise, version
/// and keep in step for no gain in what can be expressed.
/// </remarks>
public readonly record struct HostCall(HostCallKind Kind, int A = 0, int B = 0)
{
    public static HostCall Tick(PlayerAction action) => new(HostCallKind.Tick, Pack(action));

    /// <summary>The action this call carried, for a tick.</summary>
    public PlayerAction Action => Unpack(A);

    /// <summary>
    /// A PlayerAction as one integer, and back.
    /// </summary>
    /// <remarks>
    /// Packed rather than stored as three fields because the two-integer shape
    /// is what keeps this format small and uniform. The original's own encoding
    /// is preserved inside it: horizontal is 0, 4 or 8 in the low byte and
    /// vertical is 0, 1 or 2 in the next, exactly as ds:0x164E and ds:0x1650
    /// hold them, so a packed action can be read by eye against the
    /// disassembly. Fire is the bit above both.
    /// </remarks>
    public static int Pack(PlayerAction action) =>
        action.Horizontal | (action.Vertical << 8) | (action.Fire ? 1 << 16 : 0);

    public static PlayerAction Unpack(int packed) =>
        new((byte)(packed & 0xFF), (byte)((packed >> 8) & 0xFF), (packed & (1 << 16)) != 0);
    public static HostCall ToMenu() => new(HostCallKind.ToMenu);
    public static HostCall MoveSelection(int by) => new(HostCallKind.MoveSelection, by);
    public static HostCall StartSelectedWorld() => new(HostCallKind.StartSelectedWorld);
    public static HostCall Load(int world, int stage) => new(HostCallKind.Load, world, stage);
    public static HostCall Go(int by) => new(HostCallKind.Go, by);
    public static HostCall RestartCave() => new(HostCallKind.RestartCave);
    public static HostCall AbandonToChooser() => new(HostCallKind.AbandonToChooser);

    // The two settable properties that change what the host DOES. Assigning
    // them is a call like any other and used to escape the recorder entirely -
    // the browser sets Selected directly in two places, so a session that used
    // the chooser replayed with the wrong world highlighted. Astra, 19
    // September 2026.
    //
    // Message is deliberately not here. It is a string shown to the player and
    // nothing reads it back, so recording it would grow every file for
    // something that cannot change a run.
    public static HostCall SetScreen(GameScreen screen) => new(HostCallKind.SetScreen, (int)screen);
    public static HostCall SetSelected(int index) => new(HostCallKind.SetSelected, index);
}
