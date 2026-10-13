namespace Rockford.Engine;

public sealed partial class Simulation
{
    /// <summary>
    /// Interrupts in one second of cave time, from the compare at <c>0x1312A</c>.
    /// The interrupt runs at 60 Hz, so this is one second.
    /// </summary>
    public const int TicksPerSecond = 0x3C;

    /// <summary>
    /// Below this many seconds the clock starts warning. <c>0x13160</c>.
    /// </summary>
    public const int WarningSeconds = 0x0A;

    /// <summary>
    /// Interrupts since the last whole second. <c>ds:0x226C</c>.
    ///
    /// Separate from <see cref="InterruptTicks"/> on purpose: the original resets
    /// this one to zero every second at <c>0x13132</c> while the interrupt handler
    /// at <c>0x13388</c> keeps incrementing it. It is not a free-running clock.
    /// </summary>
    private int _secondTicks;          // ds:0x226C

    /// <summary>
    /// True on the one step where the clock reached zero and killed the player.
    /// The blast and the death countdown do the rest, so this is only here to be
    /// tested against and shown.
    /// </summary>
    public bool TimeExpired { get; private set; }

    /// <summary>
    /// Turns the time left into points on finishing a cave, from <c>0xEE25</c>.
    ///
    /// One point a second, and the clock runs to zero. The original does it as a
    /// visible tally rather than a sum: it waits four interrupts, adds a point,
    /// checks the bonus threshold, plays the collect sound and drops the clock by
    /// one, round and round until nothing is left. That is roughly a fifteenth of
    /// a second a point, so a cave finished with a minute on the clock spends four
    /// seconds counting itself out.
    ///
    /// This does the arithmetic in one go and returns the total, because the tally
    /// is presentation - the pacing loop at <c>0xEE2C</c> is a wait, and the sound
    /// and the redraw are the only other things in it. A front end that wants the
    /// tally can spend the number it is given.
    /// </summary>
    public int CashInTime()
    {
        if (TimeRemaining <= 0) return 0;      // 0xEE25, the signed test

        var earned = TimeRemaining;
        AwardScore(earned);
        TimeRemaining = 0;                     // 0xEE47, run down to nothing
        return earned;
    }

    /// <summary>
    /// The cave clock, from <c>0x13122</c>, called from the main loop at
    /// <c>0xEE73</c>.
    ///
    /// Three things in it are easy to get wrong:
    ///
    /// It does not run while the player is dying. <c>0x13122</c> returns
    /// immediately if the death timer is set, so the eight frames of explosion are
    /// not also eight frames of the clock running down. The interrupt counter keeps
    /// counting through them - only the countdown stops.
    ///
    /// Zero is still alive. <c>0x1313C</c> compares against zero and takes the
    /// living path when the result is greater than OR EQUAL, so the player gets a
    /// whole extra second at zero and dies when it would go to minus one.
    ///
    /// Under ten seconds it speaks. <c>0x13167</c> takes the seconds left, shifts
    /// left four and adds <c>0x200</c> to make a PIT divisor, then raises the
    /// variable holding tone. A smaller divisor is a higher note, so the warning
    /// climbs in pitch as the time runs out. That is the whole alarm: no separate
    /// sound, just this one tone walking up the scale.
    /// </summary>
    private void TickClock()
    {
        TimeExpired = false;

        // 0x13122: dying stops the clock, but not the counter behind it - that is
        // advanced by the interrupt, six times a step, before any of this runs.
        if (_playerDeathTimer != 0) return;

        // 0x1312A: not a whole second yet.
        if (_secondTicks < TicksPerSecond) return;
        _secondTicks = 0;                                  // 0x13132

        TimeRemaining--;                                   // 0x13138

        if (TimeRemaining < 0)                             // 0x1313C
        {
            // 0x13143: a kind 1 blast centred on the player, then the same
            // eight-frame countdown any other death gets.
            if (_playerCell >= 0)
                Blast(_playerCell - BlastCentreOffset, kind: 1);

            _playerDeathTimer = 8;                         // 0x13151
            _soundFlags &= 0xBF;                           // 0x13157
            TimeExpired = true;
            return;
        }

        if (TimeRemaining < WarningSeconds)                // 0x13160
        {
            VariableDivisor = (TimeRemaining << 4) + 0x200;   // 0x13167
            _soundFlags |= 0x40;                              // 0x13178
        }
        else
        {
            _soundFlags &= 0xBF;                              // 0x1317F
        }
    }
}
