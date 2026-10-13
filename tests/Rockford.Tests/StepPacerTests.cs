using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// Pacing, from the routine at <c>0x1334F</c>.
///
/// The host loop did the step's work and then waited a flat 100 ms, so the period
/// was 100 ms plus the work - measured 1031 ms per game second in VIP on a
/// headless browser, and 1112 ms under a heavier paint load. The original's
/// counter is interrupts since the last step, so its wait covers the work.
/// </summary>
public class StepPacerTests
{
    /// <summary>
    /// One turn of the loop: wait, mark the start, then do the work. Returns the
    /// clock afterwards, so a test can run a sequence of steps and read the time
    /// they took.
    /// </summary>
    private static double Turn(StepPacer pacer, double now, double work,
                              double timerLate = 0)
    {
        now += pacer.Until(now) + timerLate;
        pacer.Begin(now);
        return now + work;
    }

    [Fact]
    public void The_first_step_is_due_at_once()
    {
        // Not a tenth of a second after the cave appears.
        Assert.Equal(0, new StepPacer(0).Until(0));
    }

    [Fact]
    public void An_instant_step_waits_the_whole_hundred()
    {
        var pacer = new StepPacer(0);
        pacer.Begin(0);

        Assert.Equal(100, pacer.Until(0));
    }

    [Fact]
    public void The_work_comes_out_of_the_wait_not_on_top_of_it()
    {
        // This is the defect. Forty milliseconds of work leaves sixty to wait,
        // not a hundred.
        var pacer = new StepPacer(0);
        pacer.Begin(0);

        Assert.Equal(60, pacer.Until(40));
    }

    [Fact]
    public void A_hundred_steps_of_real_work_take_ten_seconds()
    {
        // The measurement that opened the issue, as a test. Forty milliseconds of
        // work a step used to make this 14 seconds.
        var pacer = new StepPacer(0);
        var clock = 0.0;

        for (var i = 0; i < 100; i++) clock = Turn(pacer, clock, work: 40);

        // The hundredth step's own work is still running at the end, so the
        // hundred PERIODS are 9,900 ms and the clock reads 9,940.
        Assert.Equal(9_940, clock);
    }

    [Fact]
    public void A_timer_that_overshoots_does_not_accumulate()
    {
        // Browsers round timers up and add a few milliseconds of their own. Below
        // one interrupt the original could not have told the difference, so that
        // much is absorbed and the grid stays where it is. Compounding it would
        // be the same defect again, just less of it.
        var pacer = new StepPacer(0);
        var clock = 0.0;

        for (var i = 0; i < 50; i++)
            clock = Turn(pacer, clock, work: 0, timerLate: 3);

        // Fifty steps, the last of which started three late: 4,903, not the 5,150
        // that compounding would give. Asserted from both sides - an
        // implementation that returned zero throughout would pass a bound.
        Assert.Equal(4_903, clock);
    }

    [Fact]
    public void A_step_that_overruns_waits_no_time_at_all()
    {
        var pacer = new StepPacer(0);
        pacer.Begin(0);

        Assert.Equal(0, pacer.Until(130));
    }

    [Fact]
    public void An_overrun_is_not_paid_back_by_the_next_step()
    {
        // 0x13358 reads the counter, banks it and zeroes it whatever it has
        // reached, so a step that took nine interrupts does not get a next step
        // of three. It gets a full six from where it finished.
        //
        // This class's first shape gave back a 70 ms step here, on a reading of
        // the spin alone. The three instructions after it say otherwise.
        var pacer = new StepPacer(0);
        pacer.Begin(0);
        pacer.Begin(130);          // the overrunning step's successor starts late

        Assert.Equal(100, pacer.Until(130));
    }

    [Fact]
    public void A_long_stall_is_discarded_rather_than_caught_up()
    {
        // A backgrounded tab, a garbage collection, a slow first paint. Carrying
        // the debt would hand back a run of zero waits and play the cave at
        // several times speed - through whatever was about to kill the player.
        var pacer = new StepPacer(0);
        pacer.Begin(0);

        Assert.Equal(0, pacer.Until(5_000));
        pacer.Begin(5_000);

        Assert.Equal(100, pacer.Until(5_000));
    }

    [Fact]
    public void Resuming_from_a_stall_does_not_fire_two_steps_together()
    {
        // The counter is zeroed BEFORE the step runs, at 0x13360, and the loop
        // has to match that or a player returning to a backgrounded tab gets two
        // updates inside a reaction time: one for the stall, and another the
        // instant it finishes.
        var pacer = new StepPacer(0);
        var clock = Turn(pacer, 0, work: 0);

        // The timer sleeps for five seconds instead of a tenth.
        clock = Turn(pacer, clock, work: 0, timerLate: 4_900);
        var resumed = clock;

        clock = Turn(pacer, clock, work: 0);

        Assert.Equal(100, clock - resumed);
    }

    [Fact]
    public void The_tolerance_is_one_interrupt_and_not_a_whole_step()
    {
        // Either side of one interrupt is a different behaviour, so the boundary
        // is worth naming. Inside it the grid holds; outside it the grid moves.
        var absorbed = new StepPacer(0);
        absorbed.Begin(0);
        absorbed.Begin(116);                   // 16 ms late, inside one interrupt
        Assert.Equal(84, absorbed.Until(116)); // still on the original grid

        var rehung = new StepPacer(0);
        rehung.Begin(0);
        rehung.Begin(118);                     // 18 ms late, outside it
        Assert.Equal(100, rehung.Until(118));  // the grid moved with it
    }

    [Fact]
    public void The_wait_is_never_negative()
    {
        // Each of these is genuinely overdue - the step is marked as starting at
        // zero and then asked how long is left after the work. Without the clamp
        // the first one alone returns -30.
        for (var work = 100; work <= 400; work += 7)
        {
            var pacer = new StepPacer(0);
            pacer.Begin(0);
            Assert.Equal(0, pacer.Until(work));
        }
    }

    [Fact]
    public void The_clock_origin_does_not_have_to_be_zero()
    {
        // Stopwatch.Elapsed starts at zero, but performance.now does not, and
        // neither does a pacer built partway through a session.
        var pacer = new StepPacer(1_234_567);
        pacer.Begin(1_234_567);

        Assert.Equal(60, pacer.Until(1_234_607));
    }
}
