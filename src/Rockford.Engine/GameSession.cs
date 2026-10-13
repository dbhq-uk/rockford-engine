namespace Rockford.Engine;

/// <summary>What happened at the end of a cave, and what the host should do next.</summary>
public enum CaveOutcome
{
    /// <summary>Still playing.</summary>
    Playing,

    /// <summary>Out through the exit. Load the next cave.</summary>
    Completed,

    /// <summary>Died with lives left. Reload the same cave.</summary>
    Died,

    /// <summary>Died with no lives left. Show the game over screen.</summary>
    GameOver,

    /// <summary>
    /// The fourth cave of a world is done. The original goes back to the world
    /// chooser here (<c>0xFE3E</c> calls <c>0x105E3</c>) rather than advancing to
    /// the next world - "the next world" means moving one menu selection, and
    /// there is no automatic progression between them.
    /// </summary>
    WorldCompleted,
}

/// <summary>
/// The state that outlives a single cave: lives, score, and where you are in the
/// twenty. Ported from the cave-advance routine at <c>0xFE03</c> and the death
/// countdown's tail at <c>0xEE83</c>.
///
/// This is separate from <see cref="Simulation"/> because a Simulation is one cave.
/// The original keeps these in its own globals for the same reason - <c>ds:0x2258</c>
/// for lives and <c>ds:0x16B8</c> for the stage - and they survive the cave being
/// torn down and rebuilt.
///
/// Both front ends share it, so the faithful build and VIP behave identically. None
/// of this is presentation.
/// </summary>
public sealed class GameSession
{
    /// <summary>
    /// Lives at the start of a game, and after a game over. <c>0xED23</c> and
    /// <c>0xFE32</c> both write 5.
    /// </summary>
    public const int StartingLives = 5;

    /// <summary>Caves in a world, from the stage test at <c>0xFE0C</c>.</summary>
    public const int CavesPerWorld = 4;

    public int Lives { get; private set; } = StartingLives;

    /// <summary>
    /// Points below the next ten thousand. <c>ds:0x225C</c>.
    ///
    /// The original keeps the score in two words rather than one, and it is not
    /// an arbitrary split: the low word is what the display draws, and the high
    /// word is what the bonus life is decided on.
    /// </summary>
    private int _points;

    /// <summary>
    /// How many times the score has passed ten thousand. <c>ds:0x1E00</c>.
    /// </summary>
    private int _carries;

    /// <summary>The score as a player reads it.</summary>
    public int Score => _carries * CarryAt + _points;

    /// <summary>
    /// The score as the original's status row writes it - six digits, zero padded,
    /// and stuck at <c>999999</c> once the carry count passes 99.
    ///
    /// Built from the two words rather than from <see cref="Score"/>, because that
    /// is what the display routine at <c>0x12267</c> does and it is the reason the
    /// score is held in two words at all. See <see cref="HudFormat.Score"/>.
    /// </summary>
    public string ScoreText => HudFormat.Score(_carries, _points);

    /// <summary>
    /// The number the status row shows for lives, which is one less than
    /// <see cref="Lives"/> - it counts the spares. <c>0x13895</c>.
    /// </summary>
    public int SpareLives => HudFormat.SpareLives(Lives);

    /// <summary>Where the low word rolls over, from the compare at <c>0x12238</c>.</summary>
    public const int CarryAt = 10_000;

    /// <summary>
    /// True on the award that granted a bonus life, so the host can say so. The
    /// original prints "BONUS LIFE!" - the string is in the executable.
    /// </summary>
    public bool BonusJustAwarded { get; private set; }

    /// <summary>
    /// Awards points, from <c>0x12238</c>.
    ///
    /// The original checks this after EVERY award - treasure does
    /// <c>add [225C],ax</c> at <c>0x13C3E</c> and calls straight into here at
    /// <c>0x13C42</c> - so the threshold is never reached late. Accumulating a
    /// cave's points and settling up at the end would be wrong in a way that
    /// matters: a life earned on the last gem of a cave has to exist before the
    /// death that cave might also be about to deal.
    ///
    /// The bonus comes every TWENTY thousand, not every ten. Passing ten thousand
    /// increments the carry count, and <c>0x12244</c> tests its low bit - an odd
    /// carry skips the award. So the lives land at 20,000, 40,000, 60,000. Reading
    /// only the <c>cmp ax,0x2710</c> gives ten thousand and is wrong.
    /// </summary>
    public void Award(int points)
    {
        BonusJustAwarded = false;
        if (points <= 0) return;

        _points += points;

        // A loop, not an if. The original is called after every single point, so
        // it never has more than one carry to make - but a host that awards in
        // batches could, and silently dropping the second carry would lose a life.
        while (_points >= CarryAt)
        {
            _points -= CarryAt;                 // 0x1224F
            _carries++;                         // 0x12240

            if (_carries % 2 == 0)              // 0x12244, the low-bit test
            {
                Lives++;                        // 0xF441
                BonusJustAwarded = true;
            }
        }
    }

    /// <summary>Which menu world - a menu position, not a raw world.</summary>
    public int World { get; private set; }

    /// <summary>Which cave within the world, 0 to 3. <c>ds:0x16B8</c>.</summary>
    public int Stage { get; private set; }

    /// <summary>True once the last life is gone, until <see cref="Restart"/>.</summary>
    public bool IsOver { get; private set; }

    /// <summary>
    /// True while a game is running - begun, and not yet ended by running out of
    /// lives.
    ///
    /// This is what tells the world chooser whether it is starting a game or
    /// continuing one. The original reaches that screen from two directions: at
    /// the start, and again from <c>0xFE3E</c> every time a world is finished. The
    /// second of those keeps the score and the lives, because <c>0xFE1C</c> jumps
    /// past the clears while any life remains.
    /// </summary>
    public bool InProgress { get; private set; }

    public void Begin(int world, int stage = 0)
    {
        World = world;
        Stage = stage;
        IsOver = false;
        InProgress = true;
    }

    /// <summary>
    /// A fresh game: five lives, no score, back to the first cave. The original
    /// does exactly this at <c>0xFE26</c> through <c>0xFE38</c> - it does not return
    /// to a menu, it resets and carries on.
    /// </summary>
    public void Restart()
    {
        Lives = StartingLives;

        // Both words, as 0xFE26 and 0xFE2C do. Clearing only the low one would
        // leave the carry count behind and hand the next game a bonus life it
        // had not earned.
        _points = 0;
        _carries = 0;

        Stage = 0;
        IsOver = false;
        InProgress = false;
        BonusJustAwarded = false;
    }

    /// <summary>
    /// The player died and the eight-frame countdown has finished.
    ///
    /// The original decrements the stage here (<c>0xEE8B</c>) and then falls into the
    /// advance routine, which increments it again - so the net effect is that the
    /// same cave reloads. Doing it in one place is the same behaviour without the
    /// round trip.
    /// </summary>
    public CaveOutcome Died()
    {
        Lives--;
        if (Lives > 0) return CaveOutcome.Died;

        IsOver = true;
        InProgress = false;
        return CaveOutcome.GameOver;
    }

    /// <summary>
    /// Out through the exit. From <c>0xFE08</c>: the stage advances, and a stage of
    /// four means the world is finished and it wraps back to the first cave.
    /// </summary>
    public CaveOutcome Completed()
    {
        Stage++;
        if (Stage < CavesPerWorld) return CaveOutcome.Completed;

        // 0xFE0C: a stage of four means the world is finished. The stage resets,
        // and the original hands over to the chooser rather than moving on by
        // itself. Score and lives are untouched - 0xFE1C jumps past the clears
        // while any life remains.
        Stage = 0;
        return CaveOutcome.WorldCompleted;
    }

    /// <summary>
    /// A bonus life, from <c>0xF441</c>. The game shows "BONUS LIFE!" - the string is
    /// in the executable - and there is no cap on it there, so there is none here.
    /// </summary>
    public void BonusLife() => Lives++;
}
