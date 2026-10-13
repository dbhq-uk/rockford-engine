namespace Rockford.Engine;

/// <summary>Which screen the game is on.</summary>
public enum GameScreen { Title, Menu, Playing, Interlude }

/// <summary>
/// Everything the status row shows, in one value.
///
/// One value rather than eleven bindings, and that is the point of it. Each of those
/// bindings was a line that could name the wrong property and no test could see it -
/// which is not hypothetical, because the lives field spent months showing
/// <c>Lives</c> where the original shows <c>Lives - 1</c>. Built here, the choice of
/// property is engine code and has tests; the component only draws what it is given.
/// </summary>
/// <param name="Playing">False on the title and select screens, which the original
/// shows as whole pictures with no status row over them.</param>
/// <param name="Score">Already formatted - six digits, zero padded. The format is a
/// rule off the disassembly, not a choice the markup makes. See
/// <see cref="HudFormat.Score"/>.</param>
/// <param name="SpareLives">Lives minus one, as <c>0x13895</c> draws it.</param>
public readonly record struct StatusRow(
    bool Playing,
    string Sheet,
    int Stage,
    string TreasureName,
    int SpareLives,
    int Collected,
    int Needed,
    string Score,
    int Time,
    bool ExitOpen,
    string? Message);

/// <summary>Which celebration a finished cave asks for.</summary>
public enum Sting
{
    /// <summary>Nothing finished.</summary>
    None,

    /// <summary>One of the first three caves of a world. Five seconds, played over
    /// the top of the next cave rather than waited for.</summary>
    Cave,

    /// <summary>The fourth. The whole track, and a screen that waits.</summary>
    World,
}

/// <summary>
/// What one tick of play wants the shell to do about it.
///
/// The host decides; the shell acts. Everything in here needs the browser - the
/// speaker, the canvas's sprite sheet, the keyboard's tap set - and nothing in here
/// decides anything, which is what makes the decisions testable without one.
/// </summary>
public readonly record struct PlayEffects
{
    /// <summary>The frames the step produced, oldest first. Up to six, because the
    /// sound driver runs on a 60 Hz interrupt while a step is one pass of the 10 Hz
    /// loop - playing only the last would throw five sixths of the sound away.</summary>
    public IReadOnlyList<SoundFrame> Sounds { get; init; }

    /// <summary>
    /// What the step did, for a presentation that wants to draw it - moves with a
    /// source and a destination, landings, blasts and shots.
    ///
    /// Read-only reporting after the fact. Nothing here decides anything and
    /// ignoring it entirely is a supported way to run the game, which is what the
    /// faithful build does.
    ///
    /// <para>
    /// Drained every tick whether anyone reads it or not. The simulation caps the
    /// buffer, so a host that stopped taking these would stall at the cap rather
    /// than grow - but it would also be reporting a step that had already gone.
    /// </para>
    /// </summary>
    public IReadOnlyList<Observation> Observations { get; init; }

    /// <summary>Which sting to start, after the step's own sounds.</summary>
    public Sting Sting { get; init; }

    /// <summary>The cave changed, so the shell has to hand the renderer the new
    /// world's sprite sheet before it paints.</summary>
    public bool SheetChanged { get; init; }

    /// <summary>
    /// A press-anything screen just opened, so the tap backlog has to go.
    ///
    /// Gameplay reads held keys rather than taps, so every movement key pressed
    /// during the cave is still sitting in the tap set. Without this the interlude
    /// dismisses itself on its first tick.
    /// </summary>
    public bool ClearTaps { get; init; }

    /// <summary>
    /// False when this tick only counted down a hold rather than running the cave.
    ///
    /// The shell uses it to decide whether to re-render. It is not a detail the host
    /// cares about, and it is here only because the component behaved that way
    /// before this class existed - see the note on <see cref="Tick"/>.
    /// </summary>
    public bool Stepped { get; init; }

    public static PlayEffects Nothing => new() { Sounds = [], Observations = [] };
}

/// <summary>
/// The game outside a single cave: which cave, which screen, how many lives, what
/// the status row says, and what a finished or failed cave leads to.
///
/// <para>
/// This was 400 lines inside the browser component, tangled with the canvas and the
/// speaker, and it was the reason nothing tested the host loop - issue #24. None of
/// it needs a browser. What does is behind <see cref="PlayEffects"/>: the host
/// decides and hands back a description, and the shell carries it out.
/// </para>
///
/// <para>
/// A <see cref="Simulation"/> is one cave and a <see cref="GameSession"/> is the
/// lives and the score. This is the layer between them - the part the original keeps
/// in its main loop at <c>0xEDA5</c> and its cave-advance routine at <c>0xFE03</c>.
/// </para>
/// </summary>
public sealed class GameHost
{
    /// <summary>Caves in a world, from the stage test at <c>0xFE0C</c>.</summary>
    public const int CavesPerWorld = GameSession.CavesPerWorld;

    private readonly byte[] _cellmaps;
    private readonly CaveTables _tables;
    private readonly WorldEntry[] _worlds;

    public GameHost(byte[] cellmaps, CaveTables tables, WorldEntry[] worlds)
    {
        ArgumentNullException.ThrowIfNull(cellmaps);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(worlds);

        _cellmaps = cellmaps;
        _tables = tables;
        _worlds = worlds;
    }

    /// <summary>
    /// Where every call made of this host is written down, or null.
    /// </summary>
    /// <remarks>
    /// AN OBSERVATION SURFACE, like <see cref="Observation"/>, and held to the
    /// same rule: it reads what happens and changes nothing. Null by default,
    /// so every existing caller and every test runs on exactly the path it ran
    /// on before - and <c>FingerprintTests</c> is what proves that rather than
    /// this sentence.
    ///
    /// Recorded HERE rather than at the call sites because there are two of
    /// those already - the browser and the CLI - and a recorder that has to be
    /// remembered in each is a recorder that is wrong the first time a third
    /// appears. The host is the one place every call must pass through.
    ///
    /// The calls are written down whatever they return. A refused
    /// <see cref="RestartCave"/> is a call the player made, and a replay that
    /// silently dropped it would diverge the moment the refusal stopped
    /// applying.
    /// </remarks>
    public List<HostCall>? Recorder { get; set; }

    /// <summary>How deep inside the host's own surface we are.</summary>
    private int _depth;

    /// <summary>
    /// Writes a call down if it came from OUTSIDE, and keeps count either way.
    /// </summary>
    /// <remarks>
    /// Five of the eight methods call <see cref="Load"/> themselves, and
    /// <see cref="Tick"/> reaches <see cref="Go"/> when a cave is completed.
    /// Recording both the outer call and the inner one produces a list that
    /// cannot be replayed: applying the outer call runs the inner one again by
    /// itself, and the recorded inner call then runs it a THIRD time. Finishing
    /// a cave recorded Tick, Go(1) and Load, and replaying that walked to stage
    /// 2 before coming back to stage 1 - loading a cave the session never
    /// loaded, and carrying the globals an extra time.
    ///
    /// It survived the first round of tests because loading the same cave twice
    /// is accidentally idempotent: CarryGlobalsFrom copies the same values
    /// again, so the end state matched while the route did not. Astra, 19
    /// September 2026.
    /// </remarks>
    private Nesting Outermost(HostCall call)
    {
        if (_depth == 0) Recorder?.Add(call);
        return new Nesting(this);
    }

    /// <summary>Counts a call in and out, whatever it returns or throws.</summary>
    private readonly struct Nesting : IDisposable
    {
        private readonly GameHost _host;
        public Nesting(GameHost host) { _host = host; _host._depth++; }
        public void Dispose() => _host._depth--;
    }

    /// <summary>Lives, score and where you are in the twenty.</summary>
    public GameSession Session { get; } = new();

    /// <summary>The cave being played, or null before one is loaded.</summary>
    public Simulation? Sim { get; private set; }

    /// <summary>Where the 20 by 11 window sits over the 40 by 22 cave.</summary>
    public Viewport View { get; } = new();

    /// <remarks>
    /// Backed rather than automatic so an assignment from OUTSIDE is recorded.
    /// The host sets this itself on nearly every transition; those happen
    /// inside a <see cref="Nesting"/> and are not recorded, because replaying
    /// the transition sets it again.
    /// </remarks>
    public GameScreen Screen
    {
        get => _screen;
        set
        {
            if (_depth == 0) Recorder?.Add(HostCall.SetScreen(value));
            _screen = value;
        }
    }

    private GameScreen _screen = GameScreen.Title;

    /// <summary>Which menu entry is highlighted, 0 to one less than the worlds
    /// offered. Five a page - see <see cref="WorldList"/>.</summary>
    /// <remarks>
    /// Recorded on assignment for the same reason, and this one is not
    /// hypothetical: the browser sets it directly when the chooser opens on the
    /// world just played.
    /// </remarks>
    public int Selected
    {
        get => _selected;
        set
        {
            if (_depth == 0) Recorder?.Add(HostCall.SetSelected(value));
            _selected = value;
        }
    }

    private int _selected;

    /// <summary>Which menu world is loaded - a menu position, not a raw world.</summary>
    public int World { get; private set; }

    /// <summary>Which cave within it, 0 to 3.</summary>
    public int Stage { get; private set; }

    public WorldEntry CurrentWorld => _worlds[World];

    /// <summary>
    /// The cave record, which is the RAW world number times four plus the stage.
    /// The menu maps positions to raw worlds through <c>ds:0x26F2</c> - 0, 2, 4, 6,
    /// 8 on the first page, 1, 3, 5, 7, 9 on the second - so the first page plays
    /// records 0-3, 8-11, 16-19, 24-27 and 32-35 rather than the first twenty.
    /// </summary>
    public int Record => _worlds[World].World * CavesPerWorld + Stage;

    public bool AtLastCave => _worlds.Length > 0
        && World == _worlds.Length - 1 && Stage == CavesPerWorld - 1;

    // ---- what the status row shows ------------------------------------------

    /// <summary>Treasure this cave asks for.</summary>
    public int Needed { get; private set; }

    /// <summary>Treasure taken so far.</summary>
    public int Collected { get; private set; }

    /// <summary>Seconds left.</summary>
    public int Time { get; private set; }

    public bool ExitOpen { get; private set; }

    /// <summary>What just happened, or null. "out", "caught", "game over",
    /// "world complete".</summary>
    public string? Message { get; set; }

    /// <summary>Which sprite Rockford is drawn as. The engine owns the choice
    /// because it is a draw from the game's own generator.</summary>
    public int Pose { get; private set; }

    /// <summary>Ticks of play, which is what animates the tiles.</summary>
    public int Frame { get; private set; }

    /// <summary>
    /// Steps left before the cave restarts or advances, or zero.
    ///
    /// The original holds a finished or failed cave for a moment rather than cutting
    /// straight to the next, which is what its eight-frame death countdown does.
    /// </summary>
    public int HoldUntil { get; private set; }

    /// <summary>
    /// What each world calls its treasure, by RAW world number - the ten eight-byte
    /// fields at <c>ds:0x1C81</c>, which the status row indexes with the raw world
    /// at <c>0xFEE7</c>.
    /// </summary>
    /// <remarks>
    /// This was five long and indexed by MENU position, which only worked while
    /// the menu offered five worlds in the order the five names were typed. Offer
    /// scuba and it would have read the sixth name of five. The raw world is what
    /// the original indexes with, and it does not move when the menu does.
    /// </remarks>
    public static readonly string[] TreasureNames =
        ["GOLD", "GEMS", "APPLES", "CUPS", "COINS",
         "NOTES", "SUNS", "CLUBS", "HEARTS", "JEWELS"];

    /// <summary>
    /// The whole status row in one value, so the markup has one binding rather than
    /// eleven. Every choice in here - spares rather than lives, the formatted score -
    /// is a rule with a test behind it.
    /// </summary>
    public StatusRow Status() => new(
        Playing: Screen == GameScreen.Playing,
        Sheet: _worlds.Length > 0 ? CurrentWorld.Sheet : "",
        Stage: Stage,
        TreasureName: _worlds.Length > 0 && CurrentWorld.World < TreasureNames.Length
            ? TreasureNames[CurrentWorld.World] : "treasure",
        SpareLives: Session.SpareLives,
        Collected: Collected,
        Needed: Needed,
        Score: Session.ScoreText,
        Time: Time,
        ExitOpen: ExitOpen,
        Message: Message);

    // ---- moving between caves -----------------------------------------------

    /// <summary>The chooser, which is where a finished world returns to.</summary>
    public void ToMenu()
    {
        using var nesting = Outermost(HostCall.ToMenu());
        Screen = GameScreen.Menu;
        Message = null;
    }

    /// <summary>Steps one cave, rolling into the next or previous world.</summary>
    public void Go(int by)
    {
        using var nesting = Outermost(HostCall.Go(by));
        var flat = Math.Clamp(World * CavesPerWorld + Stage + by,
                              0, _worlds.Length * CavesPerWorld - 1);
        Load(flat / CavesPerWorld, flat % CavesPerWorld);
    }

    /// <summary>
    /// Builds a cave, from the loader at <c>0x10878</c> and the per-cave clears at
    /// <c>0xFEBA</c>.
    /// </summary>
    public void Load(int world, int stage)
    {
        using var nesting = Outermost(HostCall.Load(world, stage));
        if (_worlds.Length == 0) return;

        World = Math.Clamp(world, 0, _worlds.Length - 1);
        Stage = Math.Clamp(stage, 0, CavesPerWorld - 1);

        var n = Record;
        var loaded = CaveLoader.Load(_cellmaps.AsSpan(n * CaveLoader.RecordSize, CaveLoader.RecordSize));

        Needed = _tables.Required[n];

        // The globals the original keeps outside the cave and the loader does not
        // clear - the footstep's cell and pitch, the timed wall's timestamp, and
        // both worms' headings. A Simulation is one cave, so they have to be handed
        // over or every cave starts in a state the original never reaches.
        var previous = Sim;

        Sim = new Simulation(loaded.Grid, _tables.Time[n], randomSeed: 0x04D2)
        {
            PlayerCell = loaded.PlayerCell,
            CaveHasAmoeba = loaded.HasAmoeba,
            AmoebaCap = _tables.AmoebaCap[_worlds[World].World],
            TreasureQuota = Needed,
            ScoreBeforeQuota = _tables.ScoreBefore[n],
            ScoreAfterQuota = _tables.ScoreAfter[n],

            // 0x109FB and 0x10A37: the loader records where each authored egg went,
            // because hatching at 0x13EE4 sets the worm's length and not its head.
            // -1 stands for the zero 0x10878 leaves when a cave has no egg, and it
            // is never read, because a worm with no egg has a length of zero.
            WormAHead = Math.Max(loaded.TimedEntityACell, 0),
            WormBHead = Math.Max(loaded.TimedEntityBCell, 0),
        };

        if (previous is not null) Sim.CarryGlobalsFrom(previous);

        // Frame the cave the way the original does, before anything is drawn.
        View.Frame(_worlds[World].World, Stage,
                   CaveGrid.ColumnOf(loaded.PlayerCell), CaveGrid.RowOf(loaded.PlayerCell));

        Collected = 0;
        ExitOpen = false;

        // The one deliberate behaviour change in this extraction. The component
        // never set the clock here, so the status row kept the PREVIOUS cave's
        // seconds until the first tick set them 100 ms later - and on a fresh game
        // that meant one frame of a red "time 0", because zero is under the
        // warning threshold. Loading a cave and not showing its clock is not a
        // behaviour worth preserving.
        Time = Sim.TimeRemaining;
        Message = null;
        HoldUntil = 0;
    }

    // ---- one tick of play ---------------------------------------------------

    /// <summary>
    /// One pass of the main loop while a cave is up, from <c>0xEDA5</c>.
    ///
    /// Two paths. A cave that has just ended is counting down, and this only
    /// decrements it and decides what comes next; otherwise it runs the player, the
    /// scan, the score and the outcome.
    ///
    /// <para>
    /// The order of the tail is the original's and is load-bearing twice over. The
    /// score is drained BEFORE the death is resolved, so a life earned on the last
    /// gem of a cave exists before the death that cave is about to deal. And the
    /// sting goes after the step's own sounds, not before.
    /// </para>
    /// </summary>
    public PlayEffects Tick(PlayerAction action)
    {
        // Before the early returns, deliberately. A tick with no simulation and
        // a tick inside a hold are both ticks the host was asked for, and the
        // hold counters advance on them - so a replay that skipped them would
        // come out of a death hold at a different moment.
        using var nesting = Outermost(HostCall.Tick(action));

        if (Sim is null) return PlayEffects.Nothing;

        if (HoldUntil > 0) return CountDownHold();

        Sim.PlayerTick(action);
        Sim.Step();

        // The engine owns the pose. It has to: the choice between the two idle
        // poses is a draw from the game's own generator, and a draw made out here
        // would move the whole random stream.
        Pose = Sim.PlayerPose;

        // The camera follows after the player has moved, which is where the
        // original calls it - and only on the walking path, not when firing.
        if (!action.Fire && Sim.PlayerCell >= 0)
            View.Follow(CaveGrid.ColumnOf(Sim.PlayerCell), CaveGrid.RowOf(Sim.PlayerCell));

        var sounds = Sim.TakeSoundFrames();

        // Taken here, beside the sounds, and taken EVERY step. PlayerTick ran above
        // and its observations are in this same buffer - which is why the buffer
        // drains on take rather than clearing at the top of Step, where the player's
        // own move would be wiped before the scan had even started.
        var observations = Sim.TakeObservations();

        Frame++;
        Collected = Math.Max(0, Needed - Sim.TreasureQuota);

        // Drained every step and before the death below. The score outlives the
        // cave, so the running total is the session's; the cave only reports what
        // it just awarded. Settling up at the end of a cave instead would lose the
        // points on a death, and could end a game that had just earned the life
        // that would have saved it.
        Session.Award(Sim.TakeScoreDelta());

        Time = Sim.TimeRemaining;
        ExitOpen = Sim.TreasureQuota == 0;

        var sting = Sting.None;
        var clearTaps = false;

        if (Sim.CaveComplete && Message is null)
        {
            // The time left becomes points, one a second, from 0xEE25. Awarded
            // through the session so it can carry the player past a bonus-life
            // threshold, which is exactly where the original checks it.
            //
            // Drained rather than taken from the return value. CashInTime does BOTH
            // - it adds the seconds to the cave's own delta and returns them - so
            // awarding the return value leaves the same points sitting in the delta
            // for the next drain to find. Nothing claims them today, because the
            // hold ticks never drain and the cave is thrown away before another
            // tick can, but it is a double award waiting for someone to move a
            // line. Same total, one route.
            Sim.CashInTime();
            Session.Award(Sim.TakeScoreDelta());
            Time = Sim.TimeRemaining;

            // The session says whether that was the last cave of the world, rather
            // than this being inferred from the stage - the two could disagree, and
            // "all twenty" once fired on finishing the fifth world even if the
            // player had started in it.
            if (Session.Completed() == CaveOutcome.WorldCompleted)
            {
                Message = "world complete";
                HoldUntil = 0;
                Screen = GameScreen.Interlude;
                clearTaps = true;
                sting = Sting.World;
            }
            else
            {
                Message = "out";
                HoldUntil = CaveHold;
                sting = Sting.Cave;
            }
        }
        else if (Sim.DeathComplete)
        {
            // Not on PlayerDead, which is true for the whole eight-frame countdown.
            // This fires once, when the countdown finishes, which is where the
            // original takes the life - so the explosion plays out first.
            Message = Session.Died() == CaveOutcome.GameOver ? "game over" : "caught";
            HoldUntil = Session.IsOver ? GameOverHold : CaveHold;
        }

        return new PlayEffects
        {
            Sounds = sounds,
            Observations = observations,
            Sting = sting,
            ClearTaps = clearTaps,
            Stepped = true,
        };
    }

    /// <summary>
    /// Steps a finished cave's hold down, and acts when it reaches zero.
    ///
    /// Nothing is simulated and no sound is produced, which is the point: the cave
    /// is frozen on its last frame while the player takes in what happened.
    /// </summary>
    private PlayEffects CountDownHold()
    {
        if (--HoldUntil > 0) return PlayEffects.Nothing;

        if (Session.IsOver)
        {
            // 0xFE23: the game over screen, then a fresh game. The original does
            // not go back to a menu, it resets and carries on.
            Session.Restart();

            // And it is a game, which Restart alone does not say: that clears
            // InProgress, so the replacement ran unmarked. Finish a world in it and
            // the chooser - which only preserves a run that is in progress - wiped
            // the score and lives it had just been played for. It predates the
            // restart work and the last-life restart made it easier to reach.
            // Astra, 19 September 2026.
            Session.Begin(World);
            Load(World, 0);
        }
        else if (Sim!.CaveComplete)
        {
            Go(1);
        }
        else
        {
            // The same cave again, one life down.
            Load(World, Stage);
        }

        Message = null;
        return new PlayEffects { Sounds = [], Observations = [], SheetChanged = true };
    }

    /// <summary>
    /// Steps a finished or failed cave holds for, at 10 Hz. The sting runs 5.15
    /// seconds and this is 1.2, so it plays on over the next cave rather than the
    /// screen waiting for it - twenty caves of waiting five seconds each is over a
    /// minute spent watching a finished screen.
    /// </summary>
    public const int CaveHold = 12;

    /// <summary>Longer, because a game over is worth reading.</summary>
    public const int GameOverHold = 30;

    // ---- the menu -----------------------------------------------------------

    /// <summary>
    /// Starts the highlighted world.
    ///
    /// Choosing a world starts a NEW game only when there is no game in progress.
    /// Coming back here after finishing a world is the original's own flow -
    /// <c>0xFE3E</c> calls the chooser - and it keeps the score and the lives the
    /// player arrived with.
    /// </summary>
    public void StartSelectedWorld()
    {
        using var nesting = Outermost(HostCall.StartSelectedWorld());
        Screen = GameScreen.Playing;

        if (!Session.InProgress) Session.Restart();

        Session.Begin(Selected);
        Load(Selected, 0);
    }

    /// <summary>
    /// What a player-initiated restart or abandon does. The original has neither,
    /// so this is a decision rather than a port, and it is made here rather than in
    /// the shell so it can be tested and so it is in one place.
    ///
    /// <para>
    /// <b>A restart costs a life, exactly as dying does.</b> It reloads the same
    /// cave through the same route a death takes, and a restart on the last life
    /// ends the game. Without that it was free: collect the treasure, restart,
    /// collect it again, and the score climbs with nothing spent. That sat badly
    /// beside the release cave-stepper, which was taken out for removing the
    /// stakes, and the two decisions should agree.
    /// </para>
    ///
    /// <para>
    /// Refused while a cave is mid-transition, and that takes TWO tests rather than
    /// one. A completion hold and the hold after a death both raise
    /// <see cref="HoldUntil"/>, and during a completion hold
    /// <see cref="GameSession.Stage"/> has already advanced past the finished cave
    /// while <see cref="Stage"/> still names it - restarting into that gap reloads
    /// the finished cave with the two disagreeing.
    /// </para>
    ///
    /// <para>
    /// But the eight-frame death countdown is not a hold. It lives in the
    /// simulation, in <c>_playerDeathTimer</c>, and the host does not raise
    /// <c>HoldUntil</c> until <c>DeathComplete</c> fires at the end of it. So a
    /// guard on <c>HoldUntil</c> alone accepted a restart through the whole
    /// explosion, which is exactly what this was supposed to refuse. Astra caught
    /// that on the first version of this method.
    /// </para>
    /// </summary>
    /// <returns>False when nothing happened, so the shell can leave the menu open.</returns>
    public bool RestartCave()
    {
        using var nesting = Outermost(HostCall.RestartCave());
        if (Sim is null || Screen != GameScreen.Playing) return false;
        if (HoldUntil > 0 || Sim.PlayerDead) return false;

        if (Session.Died() == CaveOutcome.GameOver)
        {
            Message = "game over";
            HoldUntil = GameOverHold;
            return true;
        }

        Message = null;
        Load(World, Stage);
        return true;
    }

    /// <summary>
    /// Leaving a cave for the world chooser, mid-game.
    ///
    /// <para>
    /// <b>This abandons the run.</b> The original only ever reaches the chooser at
    /// the start of a game or after a world is finished, and in the second case it
    /// deliberately keeps the score and the lives - <c>0xFE1C</c> jumps past the
    /// clears while any life remains. Walking out of a cave part-way through is a
    /// route it does not have, so carrying a score through it is not "what the
    /// original does", it is a new rule invented by accident.
    /// </para>
    ///
    /// <para>
    /// So the chooser keeps the distinction the original draws rather than losing
    /// it: reached by FINISHING a world it continues the run, score and lives
    /// intact, which is <c>0xFE1C</c>'s own behaviour; reached by walking out of a
    /// cave it starts a fresh game. Those are two different doors, not one door
    /// behaving inconsistently - an earlier version of this comment claimed the
    /// chooser "means one thing", which was wrong in a way worth not repeating.
    /// </para>
    ///
    /// <para>
    /// Refused mid-transition, on both tests, for the same reasons as
    /// <see cref="RestartCave"/>.
    /// </para>
    /// </summary>
    /// <returns>False when nothing happened.</returns>
    public bool AbandonToChooser()
    {
        using var nesting = Outermost(HostCall.AbandonToChooser());
        if (Screen != GameScreen.Playing || HoldUntil > 0) return false;
        if (Sim?.PlayerDead == true) return false;

        Session.Restart();
        Message = null;
        Screen = GameScreen.Menu;
        return true;
    }

    /// <summary>Moves the highlight, and reports whether it actually moved.</summary>
    /// <remarks>
    /// <para>
    /// ONE PAGE STOPS AT ITS ENDS, which is what the 1988 select screen does and
    /// what this has always done with five worlds.
    /// </para>
    /// <para>
    /// MORE THAN ONE PAGE WRAPS, so walking off either end of a page lands on the
    /// next (Dan, 18 September 2026). Stopping would leave the second page with no
    /// way in: right from the fifth box is the only door it has.
    /// </para>
    /// </remarks>
    public bool MoveSelection(int by)
    {
        using var nesting = Outermost(HostCall.MoveSelection(by));
        var count = _worlds.Length;
        if (count == 0) return false;

        var next = count > WorldList.PageSize
            ? ((Selected + by) % count + count) % count
            : Math.Clamp(Selected + by, 0, count - 1);
        if (next == Selected) return false;

        Selected = next;
        return true;
    }
}
