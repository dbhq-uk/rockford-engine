using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The sound driver, from <c>0x133A5</c>.
///
/// The driver is the tail of the 60 Hz timer interrupt at <c>0x13368</c>, not
/// something the main loop calls, so one 10 Hz step carries six driver passes.
/// These read those six through <see cref="Simulation.TakeSoundFrames"/>, which is
/// the same path both front ends use.
///
/// Two of the tests this file used to hold passed with the behaviour they claimed
/// to protect removed: one accepted either divisor from the holding tone, so a
/// permanently silent tone satisfied it. They now assert exact results at named
/// ticks.
/// </summary>
public class SoundTests
{
    /// <summary>A cave with nothing in it, and time on the clock so it cannot die.</summary>
    private static Simulation Sim(ushort seed = 1) =>
        new(new CaveGrid(), 600, randomSeed: seed);

    private static IReadOnlyList<SoundFrame> StepOnce(Simulation sim)
    {
        sim.Step();
        return sim.TakeSoundFrames();
    }

    [Fact]
    public void A_step_carries_six_frames_because_the_driver_is_an_interrupt()
    {
        // 0x13368 runs at 60 Hz and falls through into 0x133A5; the main loop waits
        // for six of those at 0x1334F. One per step was the old bug.
        Assert.Equal(Simulation.TicksPerStep, StepOnce(Sim()).Count);
    }

    [Fact]
    public void Silence_when_nothing_is_pending()
    {
        foreach (var frame in StepOnce(Sim()))
        {
            Assert.Equal(SoundEvent.None, frame.Event);
            Assert.Equal(0, frame.Divisor);
            Assert.Equal(0, frame.Frequency);
        }
    }

    [Theory]
    [InlineData(SoundEvent.Collect, 0x200)]
    [InlineData(SoundEvent.Blast, 0x4000)]
    [InlineData(SoundEvent.WallGrowing, 0x7000)]
    public void A_fixed_pitch_request_speaks_once_and_stops(SoundEvent sound, int divisor)
    {
        var sim = Sim();
        sim.Request(sound);

        var frames = StepOnce(sim);

        // Exactly one of the six speaks, and it is the first - the request was
        // already pending when the step began.
        Assert.Equal(sound, frames[0].Event);
        Assert.Equal(divisor, frames[0].Divisor);
        Assert.All(frames.Skip(1), f => Assert.Equal(SoundEvent.None, f.Event));
    }

    [Fact]
    public void The_priority_chain_serves_one_request_per_interrupt()
    {
        // Three requests raised together are served over three interrupts in
        // priority order, not three at once and not one per step.
        var sim = Sim();
        sim.Request(SoundEvent.Collect);
        sim.Request(SoundEvent.Blast);
        sim.Request(SoundEvent.WallGrowing);

        var frames = StepOnce(sim);

        Assert.Equal(SoundEvent.Collect, frames[0].Event);
        Assert.Equal(SoundEvent.Blast, frames[1].Event);
        Assert.Equal(SoundEvent.WallGrowing, frames[2].Event);
        Assert.All(frames.Skip(3), f => Assert.Equal(SoundEvent.None, f.Event));
    }

    [Fact]
    public void Raising_the_same_request_twice_plays_it_once()
    {
        var sim = Sim();
        sim.Request(SoundEvent.Blast);
        sim.Request(SoundEvent.Blast);

        var frames = StepOnce(sim);

        Assert.Equal(SoundEvent.Blast, frames[0].Event);
        Assert.All(frames.Skip(1), f => Assert.Equal(SoundEvent.None, f.Event));
    }

    // ---- the holding tones ----------------------------------------------

    [Fact]
    public void A_holding_tone_speaks_on_every_eighth_tick_and_no_other()
    {
        // This is the test that used to accept either divisor, so a permanently
        // silent tone passed it. It now names the ticks.
        var sim = Sim();
        sim.Request(SoundEvent.Tick);

        var spoke = new List<int>();
        for (var step = 0; step < 4; step++)
        {
            var frames = StepOnce(sim);
            for (var i = 0; i < frames.Count; i++)
                // The divisor, not the event. A held tone is reported on every
                // tick so the enhanced presentation knows it is still raised;
                // it SPEAKS only where there is a pitch.
                if (frames[i].Event == SoundEvent.Tick && frames[i].Divisor != 0)
                    spoke.Add(step * Simulation.TicksPerStep + i);
        }

        // 24 interrupts, and the tone speaks whenever the counter's low three bits
        // are zero. It must speak, and it must not speak on every tick.
        Assert.NotEmpty(spoke);
        Assert.Equal(24 / 8, spoke.Count);
        Assert.All(spoke, t => Assert.Equal(0, (t + 1) % 8));
    }

    [Fact]
    public void A_held_tone_is_reported_on_the_ticks_it_does_not_speak_on()
    {
        // "Held but silent" and "nothing at all" are different frames, and the
        // difference is the event rather than the divisor.
        //
        // The enhanced presentation's holding tone is one oscillator started once
        // and left running - retriggering it every pulse is what turns a sustained
        // alarm into a buzz - so it needs a frame that says the tone has finished.
        // Silence was that frame, and without this every non-speaking tick looked
        // like silence, so the oscillator was stopped 133 ms after it started and
        // restarted on the next pulse.
        var sim = Sim();
        sim.Request(SoundEvent.Tick);

        // Two steps, because the counter starts at zero and is incremented before
        // the driver runs: ticks 1 to 12, of which only 8 passes the gate.
        var frames = StepOnce(sim).Concat(StepOnce(sim)).ToList();

        Assert.All(frames, f => Assert.Equal(SoundEvent.Tick, f.Event));
        Assert.Single(frames, f => f.Divisor != 0);
        Assert.Equal(11, frames.Count(f => f.Divisor == 0));
    }

    [Fact]
    public void A_tone_that_is_no_longer_raised_reports_silence()
    {
        // The other half of the pair, driven by the clock's own warning tone
        // rather than a test poking the flags: 0x13178 raises it inside the last
        // ten seconds and 0x1317F clears it outside them. Clearing has to be
        // visible, or the enhanced presentation never stops the oscillator.
        var sim = new Simulation(new CaveGrid(), 5, randomSeed: 1);

        // Ten steps is sixty interrupts, which is one second on the clock.
        for (var i = 0; i < 10; i++) StepOnce(sim);
        Assert.Equal(0x40, sim.SoundFlags & 0x40);
        Assert.All(StepOnce(sim), f => Assert.Equal(SoundEvent.VariableTone, f.Event));

        sim.TimeRemaining = 600;
        for (var i = 0; i < 10; i++) StepOnce(sim);

        Assert.Equal(0, sim.SoundFlags & 0x40);
        Assert.All(StepOnce(sim), f => Assert.Equal(SoundEvent.None, f.Event));
    }

    [Fact]
    public void A_held_tone_reported_silent_is_still_silent_to_the_hardware()
    {
        // The faithful presentation reads the divisor and nothing else, so this
        // change has to be invisible to it. Zero is silence either way.
        var sim = Sim();
        sim.Request(SoundEvent.Tick);

        var frames = StepOnce(sim).Concat(StepOnce(sim)).ToList();

        Assert.Equal(1, frames.Count(f => Simulation.FrequencyOf(f.Divisor) > 0));
    }

    [Fact]
    public void A_held_tone_does_not_gag_a_sound_underneath_it()
    {
        // The report is the LAST thing the chain does, after every one-shot has
        // had its turn. Putting it earlier would have made the held tone silence
        // the treasure and boulder sounds it is supposed to play over.
        var sim = Sim();
        sim.Request(SoundEvent.Tick);
        sim.Request(SoundEvent.Collect);

        var frames = StepOnce(sim);

        Assert.Contains(frames, f => f.Event == SoundEvent.Collect && f.Divisor == 0x200);
    }

    [Fact]
    public void The_whole_step_is_exact_while_a_tick_holds()
    {
        // Every frame of two steps, event AND divisor. The tests around this one
        // each pin a part of the driver and between them still left three things
        // loose: the tone's own pitch, which interrupt a competing request lands
        // on, and whether the tone is reported at all on the ticks it is silent.
        var sim = Sim();
        sim.Request(SoundEvent.Tick);
        sim.Request(SoundEvent.Collect);

        var first = StepOnce(sim);
        var second = StepOnce(sim);

        // Interrupt 1: the alarm is gated off, so the Collect underneath it speaks
        // rather than being gagged. Interrupts 2 to 6: held, and silent.
        Assert.Equal(
            [(SoundEvent.Collect, 0x200), (SoundEvent.Tick, 0), (SoundEvent.Tick, 0),
             (SoundEvent.Tick, 0), (SoundEvent.Tick, 0), (SoundEvent.Tick, 0)],
            first.Select(f => (f.Event, f.Divisor)).ToArray());

        // Interrupt 8 is the first with three clear low bits, and 0x3E8 is the
        // pitch it speaks at.
        Assert.Equal(
            [(SoundEvent.Tick, 0), (SoundEvent.Tick, 0x3E8), (SoundEvent.Tick, 0),
             (SoundEvent.Tick, 0), (SoundEvent.Tick, 0), (SoundEvent.Tick, 0)],
            second.Select(f => (f.Event, f.Divisor)).ToArray());
    }

    [Fact]
    public void The_whole_step_is_exact_while_the_variable_tone_holds()
    {
        // The same again for 0x40, which had no test of its emitted sound at all -
        // the clock tests check VariableDivisor, which is the number going IN.
        var sim = Sim();
        sim.VariableDivisor = 0x2A0;
        sim.Request(SoundEvent.VariableTone);

        var first = StepOnce(sim);
        var second = StepOnce(sim);

        Assert.All(first, f => Assert.Equal((SoundEvent.VariableTone, 0), (f.Event, f.Divisor)));

        Assert.Equal(
            [(SoundEvent.VariableTone, 0), (SoundEvent.VariableTone, 0x2A0),
             (SoundEvent.VariableTone, 0), (SoundEvent.VariableTone, 0),
             (SoundEvent.VariableTone, 0), (SoundEvent.VariableTone, 0)],
            second.Select(f => (f.Event, f.Divisor)).ToArray());
    }

    [Fact]
    public void The_alarm_outranks_the_variable_tone_when_both_speak()
    {
        // 0x133BF is tested before 0x133EC, and they share the eighth-tick gate -
        // so on the tick where both would speak, only the alarm does.
        var sim = Sim();
        sim.Request(SoundEvent.Tick);
        sim.Request(SoundEvent.VariableTone);

        StepOnce(sim);
        var second = StepOnce(sim);

        Assert.Equal((SoundEvent.Tick, 0x3E8), (second[1].Event, second[1].Divisor));
        Assert.DoesNotContain(second, f => f.Divisor != 0 && f.Event == SoundEvent.VariableTone);
    }

    [Fact]
    public void A_holding_tone_does_not_clear_itself()
    {
        // Neither 0x80 nor 0x40 clears its own bit - they hold until whatever
        // raised them stops.
        var sim = Sim();
        sim.Request(SoundEvent.Tick);

        for (var i = 0; i < 5; i++) StepOnce(sim);

        Assert.Equal(0x80, sim.SoundFlags & 0x80);
    }

    [Fact]
    public void A_gated_off_holding_tone_does_not_gag_the_sounds_beneath_it()
    {
        // 0x133C7 jumps to the NEXT priority test rather than returning, so on the
        // seven ticks in eight where the alarm cannot speak, everything below it
        // still can. Returning silence there - which the port used to do - made a
        // pending alarm mute collection, blasts and falling rocks entirely.
        var sim = Sim();
        sim.Request(SoundEvent.Tick);        // the alarm, mostly silent
        sim.Request(SoundEvent.Collect);     // lower priority, must still be heard

        var frames = StepOnce(sim);

        Assert.Contains(frames, f => f.Event == SoundEvent.Collect);
    }

    [Fact]
    public void The_higher_holding_tone_wins_when_both_can_speak()
    {
        var sim = Sim();
        sim.Request(SoundEvent.Tick);
        sim.Request(SoundEvent.VariableTone);

        var spoken = new List<SoundEvent>();
        for (var i = 0; i < 3; i++)
            spoken.AddRange(StepOnce(sim).Select(f => f.Event).Where(e => e != SoundEvent.None));

        Assert.Contains(SoundEvent.Tick, spoken);
        Assert.DoesNotContain(SoundEvent.VariableTone, spoken);
    }

    // ---- the draws -------------------------------------------------------

    [Theory]
    [InlineData(SoundEvent.TreasureFalling, 0x80, 0xC0)]
    [InlineData(SoundEvent.BoulderFalling, 0x4400, 0x4800)]
    [InlineData(SoundEvent.LowRumble, 0x5000, 0x5800)]
    public void A_two_pitch_request_picks_one_of_its_two(SoundEvent sound, int a, int b)
    {
        for (ushort seed = 0; seed < 64; seed++)
        {
            var sim = Sim(seed);
            sim.Request(sound);

            var frame = StepOnce(sim).First(f => f.Event == sound);

            Assert.True(frame.Divisor == a || frame.Divisor == b,
                        $"seed {seed} gave {frame.Divisor:X}");
        }
    }

    [Theory]
    [InlineData(SoundEvent.TreasureFalling, 0x80, 0xC0)]
    [InlineData(SoundEvent.BoulderFalling, 0x4400, 0x4800)]
    public void A_drawn_pitch_actually_uses_both_of_its_two(SoundEvent sound, int a, int b)
    {
        // If the draw were read at the wrong width, or the bit test inverted, every
        // event would land on one pitch and the either-divisor test above would not
        // notice. It used to cover the boulder only, so treasure could have been
        // pinned to one pitch with nothing failing.
        //
        // The footstep is deliberately absent. It is a two-pitch handler and it is
        // NOT a drawn one - 0x13453 alternates on a toggle byte - so varying the
        // seed cannot move it, and its distribution is asserted over successive
        // requests in FootstepTests instead.
        var seen = new HashSet<int>();
        for (ushort seed = 0; seed < 64; seed++)
        {
            var sim = Sim(seed);
            sim.Request(sound);
            seen.Add(StepOnce(sim).First(f => f.Event == sound).Divisor);
        }

        Assert.Equal([a, b], seen.OrderBy(v => v).ToArray());
    }

    [Fact]
    public void Only_the_two_pitch_handlers_draw()
    {
        // The draw belongs to the driver, so it must happen for the handlers that
        // choose a pitch and not for the ones with a fixed note.
        var drawing = Sim();
        drawing.Request(SoundEvent.BoulderFalling);
        drawing.Step();

        var quiet = Sim();
        quiet.Request(SoundEvent.Collect);
        quiet.Step();

        Assert.NotEqual(quiet.Random.Seed, drawing.Random.Seed);
    }

    [Fact]
    public void Taking_the_frames_twice_does_not_serve_them_twice()
    {
        // Both presentations read this one list. A front end that called again
        // would otherwise consume the requests and draw from the generator a
        // second time.
        var sim = Sim();
        sim.Request(SoundEvent.Blast);
        sim.Step();

        var first = sim.TakeSoundFrames();
        var second = sim.TakeSoundFrames();

        Assert.NotEmpty(first);
        Assert.Empty(second);
    }

    [Fact]
    public void A_step_serves_its_requests_whether_or_not_anyone_listens()
    {
        // The frames are produced by the driver inside Step, not by the taking of
        // them, so a front end that does not drain - the host skips the call when
        // the sound is muted - must not change the simulation.
        //
        // This is the invariant behind the drain test rather than a restatement of
        // it: two of the handlers draw from the shared generator while serving, so
        // if servicing were deferred to the take, muting the game would move every
        // later random event in the cave.
        var drained = Sim();
        var ignored = Sim();

        for (var i = 0; i < 5; i++)
        {
            drained.Request(SoundEvent.BoulderFalling);
            ignored.Request(SoundEvent.BoulderFalling);

            drained.Step();
            drained.TakeSoundFrames();
            ignored.Step();                       // never taken, as a muted host does
        }

        // Against an independent generator rather than against each other. Two
        // simulations that both drew an extra number would agree with one another
        // and be equally wrong - which is exactly what an extra draw per boulder
        // sound does.
        var expected = new GameRandom(1);
        for (var i = 0; i < 5; i++) expected.Next();

        Assert.Equal(expected.Seed, drained.Random.Seed);
        Assert.Equal(expected.Seed, ignored.Random.Seed);
        Assert.Equal(0, ignored.SoundFlags);      // and the requests were still served
    }

    [Fact]
    public void A_step_does_not_hand_back_the_step_before_it()
    {
        // Step clears the list before it fills it, so an undrained step cannot
        // leave its frames in front of the next one's.
        var sim = Sim();
        sim.Request(SoundEvent.Blast);
        sim.Step();                               // not taken

        sim.Request(SoundEvent.Collect);
        var frames = StepOnce(sim);

        Assert.Equal(Simulation.TicksPerStep, frames.Count);
        Assert.Equal(SoundEvent.Collect, frames[0].Event);
        Assert.DoesNotContain(frames, f => f.Event == SoundEvent.Blast);
    }

    [Fact]
    public void A_divisor_becomes_the_frequency_the_hardware_would_have_made()
    {
        // 1193180 / 512, the treasure pickup.
        Assert.Equal(2330.4, Simulation.FrequencyOf(0x200), 1);
        Assert.Equal(0, Simulation.FrequencyOf(0));
    }
}
