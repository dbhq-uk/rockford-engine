using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The cave clock, from <c>0x13122</c>.
///
/// It was loaded and never decremented, so every cave allowed unlimited time and
/// nothing failed - a probe starting with one second still had one second after a
/// hundred steps. These pin the whole routine rather than only that it counts:
/// the second boundary, the extra second at zero, the warning tone and its pitch,
/// and that a death stops it.
/// </summary>
public class ClockTests
{
    private static Simulation Sim(int seconds)
    {
        var sim = new Simulation(new CaveGrid(), seconds, randomSeed: 1);
        sim.PlayerCell = CaveGrid.AddressOf(5, 5);
        return sim;
    }

    /// <summary>Steps needed to cross one second: 60 interrupts at 6 a step.</summary>
    private const int StepsPerSecond = Simulation.TicksPerSecond / Simulation.TicksPerStep;

    [Fact]
    public void One_second_is_sixty_interrupts_and_not_one_step()
    {
        var sim = Sim(60);

        for (var i = 0; i < StepsPerSecond - 1; i++) sim.Step();
        Assert.Equal(60, sim.TimeRemaining);      // not yet

        sim.Step();
        Assert.Equal(59, sim.TimeRemaining);      // exactly on the boundary
    }

    [Fact]
    public void The_clock_keeps_running_second_after_second()
    {
        var sim = Sim(60);

        for (var i = 0; i < StepsPerSecond * 10; i++) sim.Step();

        Assert.Equal(50, sim.TimeRemaining);
    }

    [Fact]
    public void Zero_is_still_alive_for_one_more_second()
    {
        // 0x1313C compares against zero and takes the living path on greater than
        // OR EQUAL, so reaching zero does not kill - going below it does.
        var sim = Sim(1);

        for (var i = 0; i < StepsPerSecond; i++) sim.Step();
        Assert.Equal(0, sim.TimeRemaining);
        Assert.False(sim.PlayerDead);
        Assert.False(sim.TimeExpired);
    }

    [Fact]
    public void Running_out_kills_the_player()
    {
        var sim = Sim(0);

        for (var i = 0; i < StepsPerSecond; i++) sim.Step();

        Assert.True(sim.TimeExpired);
        Assert.True(sim.PlayerDead);
    }

    [Fact]
    public void Running_out_is_one_pulse_and_not_a_latch()
    {
        var sim = Sim(0);
        for (var i = 0; i < StepsPerSecond; i++) sim.Step();
        Assert.True(sim.TimeExpired);

        sim.Step();

        Assert.False(sim.TimeExpired);
    }

    [Fact]
    public void A_death_stops_the_countdown()
    {
        // 0x13122 returns immediately while the death timer is set.
        //
        // A death lasts fewer steps than a second does, so simply dying and
        // stepping proves nothing - the clock would be quiet anyway. This walks to
        // one step short of the boundary first, so the death spans a tick that
        // WOULD have landed, and the clock still does not move.
        var sim = Sim(60);
        for (var i = 0; i < StepsPerSecond - 1; i++) sim.Step();
        Assert.Equal(60, sim.TimeRemaining);

        sim.Blast(sim.PlayerCell - Simulation.BlastCentreOffset, kind: 1);

        // Eight steps: seven with the timer live, and the eighth where it reaches
        // zero. The clock is skipped on all of them because it runs before the
        // countdown does.
        for (var i = 0; i < 8; i++) sim.Step();

        Assert.False(sim.PlayerDead);
        Assert.Equal(60, sim.TimeRemaining);
    }

    [Fact]
    public void The_counter_behind_the_clock_keeps_running_through_a_death()
    {
        // "Dying stops the countdown but not the interrupt counter." The interrupt
        // at 0x13388 increments ds:0x226C regardless; only the decrement at
        // 0x13138 is skipped. So the time owed is paid the moment the death ends,
        // rather than the cave quietly gaining eight steps of grace.
        var sim = Sim(60);
        for (var i = 0; i < StepsPerSecond - 1; i++) sim.Step();

        sim.Blast(sim.PlayerCell - Simulation.BlastCentreOffset, kind: 1);
        for (var i = 0; i < 8; i++) sim.Step();
        Assert.Equal(60, sim.TimeRemaining);

        // The very next living step settles it, because the counter is long past
        // sixty by now.
        sim.Step();

        Assert.Equal(59, sim.TimeRemaining);
    }

    // ---- the warning ----------------------------------------------------

    [Theory]
    [InlineData(9)]
    [InlineData(5)]
    [InlineData(1)]
    public void Under_ten_seconds_the_clock_speaks(int seconds)
    {
        var sim = Sim(seconds + 1);

        for (var i = 0; i < StepsPerSecond; i++) sim.Step();

        Assert.Equal(seconds, sim.TimeRemaining);
        Assert.Equal(0x40, sim.SoundFlags & 0x40);
    }

    [Fact]
    public void Ten_seconds_and_above_is_silent()
    {
        var sim = Sim(12);

        for (var i = 0; i < StepsPerSecond; i++) sim.Step();

        Assert.Equal(11, sim.TimeRemaining);
        Assert.Equal(0, sim.SoundFlags & 0x40);
    }

    [Fact]
    public void The_warning_climbs_in_pitch_as_the_time_runs_out()
    {
        // 0x13167: divisor = (seconds << 4) + 0x200. A smaller divisor is a higher
        // note, so fewer seconds means a higher warning. Asserting the exact
        // divisors matters - a test that only checked the tone was raised would
        // pass with the pitch constant, and the rising alarm is the whole point.
        var pitches = new List<(int seconds, int divisor)>();

        var sim = Sim(10);
        for (var second = 0; second < 9; second++)
        {
            for (var i = 0; i < StepsPerSecond; i++) sim.Step();
            pitches.Add((sim.TimeRemaining, sim.VariableDivisor));
        }

        foreach (var (seconds, divisor) in pitches)
            Assert.Equal((seconds << 4) + 0x200, divisor);

        // And it really does move, in the right direction.
        Assert.True(pitches[^1].divisor < pitches[0].divisor);
        Assert.Equal(9, pitches.Select(p => p.divisor).Distinct().Count());
    }

    [Fact]
    public void Running_out_clears_the_warning_tone()
    {
        // 0x13157 clears bit 0x40 on the way out, or the alarm carries on sounding
        // over the explosion that it caused.
        var sim = Sim(1);

        for (var i = 0; i < StepsPerSecond * 2; i++) sim.Step();

        Assert.True(sim.TimeExpired || sim.PlayerDead);
        Assert.Equal(0, sim.SoundFlags & 0x40);
    }

    [Fact]
    public void A_time_bonus_pushes_the_warning_back_above_ten()
    {
        var sim = Sim(5);
        for (var i = 0; i < StepsPerSecond; i++) sim.Step();
        Assert.Equal(0x40, sim.SoundFlags & 0x40);     // warning on

        sim.TimeRemaining += 0x14;                     // the clock collectible
        for (var i = 0; i < StepsPerSecond; i++) sim.Step();

        Assert.Equal(0, sim.SoundFlags & 0x40);        // and off again
    }

    // ---- the constructor -------------------------------------------------

    [Fact]
    public void The_second_argument_is_the_clock_and_the_third_is_the_seed()
    {
        // Argument order is load-bearing here and the compiler cannot help: the
        // clock is an int and the seed is a ushort, so `new Simulation(g, 1)`
        // reads as one SECOND and silently leaves the seed at zero. It used to
        // read as seed one. Anything ported across without looking gets a cave
        // that dies after ten steps for no visible reason.
        var sim = new Simulation(new CaveGrid(), 90, randomSeed: 0x1234);

        Assert.Equal(90, sim.TimeRemaining);
        Assert.Equal(0x1234, sim.Random.Seed);
    }

    [Fact]
    public void A_cave_with_no_time_kills_the_player()
    {
        // The behaviour that made the time a required argument. It is faithful -
        // a cave authored with no seconds would do exactly this - and it was a
        // trap, because the clock has to run a whole second before it shows, so
        // a test taking nine steps passed and the same test taking ten did not.
        //
        // No authored cave is like this: all forty entries in the table at
        // 0x26A2 are non-zero, running 80 to 180 seconds. Zero is now something
        // a caller has to ask for.
        var sim = new Simulation(new CaveGrid(), 0);

        for (var i = 0; i < StepsPerSecond - 1; i++) sim.Step();
        Assert.False(sim.PlayerDead, "it died before the first second was up");

        sim.Step();

        // All three, because TimeExpired alone does not prove a death. This is
        // also the no-player-cell path: 0xEE9E skips the blast when there is
        // nobody to centre it on, and the countdown runs anyway.
        Assert.True(sim.TimeExpired);
        Assert.True(sim.PlayerDead);
        Assert.Equal(-1, sim.TimeRemaining);
    }
}
