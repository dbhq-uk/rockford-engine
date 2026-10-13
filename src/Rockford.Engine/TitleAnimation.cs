namespace Rockford.Engine;

/// <summary>Which frame each of the title screen's four layers is showing.</summary>
/// <param name="Arm">The arm and hand, 0 to 4.</param>
/// <param name="Diamond">The gem in flight, 0 to 8.</param>
/// <param name="Face">The expression, 0 to 8.</param>
/// <param name="AlternateFace">True to take the face from the second column of
/// expressions rather than the first.</param>
/// <param name="Foot">The tapping shoe, 0 to 3.</param>
public readonly record struct TitleFrame(
    int Arm, int Diamond, int Face, bool AlternateFace, int Foot);

/// <summary>
/// The title screen animation, from <c>0xF29C</c>.
///
/// The title is not a still: Rockford blinks, taps a foot and tosses a diamond up
/// and catches it. The frames are in <c>TITLEANI.SCM</c>, which the loader at
/// <c>0x12922</c> puts on the page after the title picture, and four routines blit
/// from there - <c>0xEF4F</c> the arm, <c>0xF014</c> the gem, <c>0xF0C9</c> the face
/// and <c>0xF1BE</c> the foot.
///
/// <para>
/// This class is only the counters, because the counters are the rules and the
/// rectangles are data - <c>tools/webassets.py</c> reads those out of the executable
/// into <c>titleani.json</c> rather than anyone typing them.
/// </para>
///
/// <code>
/// f2b4: mov  WORD PTR cs:0x2,0x0
/// f2bb: mov  WORD PTR ds:0x16a8,0x0
/// f2c1: cmp  WORD PTR cs:0x2,0x7
/// f2c7: jb   0xf2c1                  ; wait seven interrupts
/// f2c9: mov  WORD PTR cs:0x2,0x0
/// f2d0: call 0xef4f                  ; then draw the four layers
/// f2d3: call 0xf014
/// f2d6: call 0xf0c9
/// f2d9: call 0xf1be
/// f2dc: call 0x117e5                 ; and a press ends it
/// ...
/// f366: cmp  WORD PTR ds:0x16a8,0x4b0
/// f36c: jae  0xf371                  ; so does twenty seconds
/// </code>
/// </summary>
public sealed class TitleAnimation
{
    /// <summary>
    /// Interrupts between frames, from the spin at <c>0xF2C1</c>. Seven of sixty,
    /// so the animation runs at about 8.6 frames a second - noticeably slower than
    /// the 10 Hz the cave runs at, and the difference is visible.
    /// </summary>
    public const int TicksPerFrame = 7;

    /// <summary>
    /// Interrupts before the animation gives up and the attract screen comes round
    /// again. <c>0xF366</c>, and 1200 at 60 Hz is twenty seconds.
    /// </summary>
    public const int TicksBeforeGivingUp = 0x4B0;

    /// <summary>Frames in the toss, from the turn at <c>0xF30D</c>.</summary>
    public const int TossFrames = 9;

    /// <summary>Frames the arm has, from the clamp at <c>0xEF74</c>. Fewer than the
    /// toss, so the arm holds its last position while the gem is still rising.</summary>
    public const int ArmFrames = 5;

    /// <summary>Frames the foot has, from the turn at <c>0xF33D</c>.</summary>
    public const int FootFrames = 4;

    private readonly GameRandom _random;

    private int _toss;          // ds:0x2E22
    private int _tossStep = 1;  // ds:0x2E26
    private int _foot;          // ds:0x2E20
    private int _footStep = 1;  // ds:0x2E24
    private bool _alternate;    // ds:0x2E28
    private int _ticks;         // ds:0x16A8, zeroed at 0xF2BB

    /// <summary>
    /// The generator is the game's own, and the animation really does draw from it -
    /// <c>0xF2FF</c> calls <c>0x124B1</c> once per toss. So a player who sits on the
    /// title screen leaves the stream somewhere else, and the first cave they play
    /// is not the cave they would have got by pressing a key at once.
    /// </summary>
    public TitleAnimation(GameRandom random) => _random = random;

    /// <summary>Interrupts elapsed since the animation started.</summary>
    public int Ticks => _ticks;

    /// <summary>True once twenty seconds are up, from <c>0xF366</c>.</summary>
    public bool Finished => _ticks >= TicksBeforeGivingUp;

    /// <summary>What to draw now.</summary>
    public TitleFrame Frame => new(
        // 0xEF74 clamps the arm and lets the other two run on.
        Arm: Math.Min(_toss, ArmFrames - 1),
        Diamond: _toss,
        Face: _toss,
        AlternateFace: _alternate,
        Foot: _foot);

    /// <summary>
    /// One interrupt. Returns true on the ticks a new frame is due, which is every
    /// seventh.
    /// </summary>
    public bool Tick()
    {
        _ticks++;

        if (_ticks % TicksPerFrame != 0) return false;

        Advance();
        return true;
    }

    /// <summary>
    /// The two counters, from <c>0xF2E6</c> to <c>0xF365</c>.
    ///
    /// Both ping-pong rather than looping, which is the whole reason the hand goes
    /// up and comes back down instead of snapping to the bottom. And both hold
    /// their turn for one extra frame, because the code writes the value one inside
    /// the end rather than the end itself.
    /// </summary>
    private void Advance()
    {
        var next = _toss + _tossStep;

        // 0xF2ED. At four the alternate expression is dropped, and on the way DOWN
        // one toss in four picks it up again - a draw from the shared generator,
        // testing the low two bits.
        if (next == 4)
        {
            _alternate = false;                                 // 0xF2F2
            if (_tossStep == -1)                                // 0xF2F7
                _alternate = (_random.Next() & 3) == 0;         // 0xF2FF, 0xF307
        }

        if (next >= TossFrames) { _toss = TossFrames - 1; _tossStep = -1; }   // 0xF312
        else if (next < 0) { _toss = 0; _tossStep = 1; }                      // 0xF325
        else _toss = next;                                                    // 0xF333

        // 0xF336. The foot turns at 2 and 1 rather than 3 and 0, so its ends are
        // held the same way the toss's are.
        next = _foot + _footStep;

        if (next >= FootFrames) { _foot = 2; _footStep = -1; }                // 0xF342
        else if (next < 0) { _foot = 1; _footStep = 1; }                      // 0xF355
        else _foot = next;                                                    // 0xF363
    }
}
