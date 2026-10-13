namespace Rockford.Engine;

/// <summary>
/// When the next simulation step is due, from the pacing routine at
/// <c>0x1334F</c>, which the main loop calls first at <c>0xEDA5</c>.
///
/// <code>
/// 1334f: mov  ax,cs:0x2
/// 13353: cmp  ax,0x6
/// 13356: jb   0x1334f
/// 13358: mov  ax,cs:0x2
/// 1335c: add  ds:0x2c8c,ax
/// 13360: mov  WORD PTR cs:0x2,0
/// 13367: ret
/// </code>
///
/// <c>cs:0x2</c> is interrupts since the counter was last zeroed. The routine
/// spins until it has reached six, and then zeroes it - so the wait covers the
/// previous step's own work, which was running while the counter ticked. The
/// host loop instead did the work and THEN waited a flat 100 ms, making the
/// period 100 ms plus the work: measured 1031 ms per game second in VIP on a
/// headless browser, and 1112 ms under a heavier paint load. VIP was worse
/// because its larger canvas put drawing time straight into simulation time.
///
/// Two details of that routine decide everything else here.
///
/// It spins until the counter is at least six rather than exactly six, and then
/// zeroes it. A step that overran does not get a shorter next step to make up
/// for it, and it does not get a burst of catch-up steps either: the excess
/// interrupts are simply thrown away. So any overrun re-hangs the grid from
/// where it finished. Allowing an overrun to be paid back by the next step was
/// this class's first shape and it was wrong - the original has no such debt.
///
/// And the counter is zeroed BEFORE the step runs, not after it. That is why
/// <see cref="Begin"/> exists as a separate call: a loop that re-hangs the grid
/// after the work has already happened lets a step that resumes from a long
/// stall be followed immediately by another, so a player coming back to a
/// backgrounded tab gets two updates inside a reaction time.
///
/// Pure arithmetic over a clock the caller supplies, so it can be tested without
/// a browser or a stopwatch.
/// </summary>
public sealed class StepPacer
{
    /// <summary>A step is six interrupts at 60 Hz - a tenth of a second.</summary>
    public const double StepMilliseconds = 100.0;

    /// <summary>
    /// One interrupt, and the finest interval the original can measure.
    ///
    /// It is also the tolerance for calling a late wake-up "on time". A browser
    /// timer that fires two milliseconds late every step would otherwise push
    /// the deadline two milliseconds further out every step and run the game
    /// slow all over again, just less of it. Below one interrupt the original
    /// could not have told the difference, so absorbing it is not a liberty.
    /// </summary>
    public const double InterruptMilliseconds = StepMilliseconds / 6.0;

    /// <summary>
    /// The clock reading at which the counter was last zeroed - <c>0x13360</c>.
    /// </summary>
    private double _zeroed;

    /// <param name="now">The clock reading as the loop starts.</param>
    public StepPacer(double now)
    {
        // A step behind, so the first one is due at once rather than a tenth of
        // a second after the cave appears.
        _zeroed = now - StepMilliseconds;
    }

    /// <summary>
    /// How long to wait before the next step may begin, in milliseconds. Never
    /// negative; zero means it is due now.
    ///
    /// This is the spin at <c>0x1334F</c>.
    /// </summary>
    public double Until(double now)
    {
        var wait = _zeroed + StepMilliseconds - now;
        return wait > 0 ? wait : 0;
    }

    /// <summary>
    /// The wait is over and the step is starting. <c>0x13360</c>.
    /// </summary>
    public void Begin(double now)
    {
        var due = _zeroed + StepMilliseconds;

        // Within one interrupt of the deadline counts as on it, which keeps the
        // grid absolute against timer jitter. Anything later is a genuine
        // overrun or a stall, and the excess is discarded exactly as zeroing the
        // counter discards it.
        _zeroed = now - due <= InterruptMilliseconds ? due : now;
    }
}
