using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The title screen animation, from <c>0xF29C</c>.
///
/// The title was a still in both builds for as long as the port existed, and
/// <c>TITLEANI.SCM</c> was extracted, shipped and read by nothing. These cover the
/// counters; the rectangles come out of the executable in <c>tools/webassets.py</c>.
/// </summary>
public class TitleAnimationTests
{
    private static TitleAnimation Fresh(ushort seed = 0) => new(new GameRandom(seed));

    /// <summary>Runs one frame's worth of interrupts and returns what to draw.</summary>
    private static TitleFrame NextFrame(TitleAnimation a)
    {
        for (var i = 0; i < TitleAnimation.TicksPerFrame; i++)
            if (a.Tick()) return a.Frame;

        throw new Xunit.Sdk.XunitException("seven ticks produced no frame");
    }

    // ---- the gate ---------------------------------------------------------

    [Fact]
    public void A_frame_is_due_every_seventh_interrupt()
    {
        // 0xF2C1 spins until cs:0x2 reaches seven, so the animation runs at 60/7,
        // about 8.6 frames a second - slower than the 10 Hz cave, and visibly so.
        var a = Fresh();

        for (var i = 1; i <= 21; i++)
            Assert.Equal(i % TitleAnimation.TicksPerFrame == 0, a.Tick());
    }

    [Fact]
    public void Seven_is_the_gate()
    {
        Assert.Equal(7, TitleAnimation.TicksPerFrame);
    }

    // ---- the toss ---------------------------------------------------------

    [Fact]
    public void The_diamond_goes_up_and_comes_back_down()
    {
        // 0xF312 and 0xF325. It ping-pongs rather than looping, which is the whole
        // reason the hand comes back down instead of snapping to the bottom - and
        // both ends are HELD for an extra frame, because the code writes the value
        // one inside the end rather than the end itself.
        var a = Fresh();
        var seen = new List<int>();

        for (var i = 0; i < 20; i++) seen.Add(NextFrame(a).Diamond);

        Assert.Equal(
            [1, 2, 3, 4, 5, 6, 7, 8, 8, 7, 6, 5, 4, 3, 2, 1, 0, 0, 1, 2],
            seen);
    }

    [Fact]
    public void The_arm_stops_before_the_diamond_does()
    {
        // 0xEF74 clamps the arm's index to four while the gem and the face run on
        // to eight. So the arm holds its last reach while the gem is still rising,
        // which is what makes the throw read as a throw.
        var a = Fresh();
        var arms = new List<int>();
        var gems = new List<int>();

        for (var i = 0; i < 8; i++)
        {
            var f = NextFrame(a);
            arms.Add(f.Arm);
            gems.Add(f.Diamond);
        }

        Assert.Equal([1, 2, 3, 4, 4, 4, 4, 4], arms);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], gems);
        Assert.Equal(TitleAnimation.ArmFrames - 1, arms.Max());
    }

    // ---- the foot ---------------------------------------------------------

    [Fact]
    public void The_foot_taps_on_its_own_counter()
    {
        // 0xF336 reads ds:0x2E20, not the toss's ds:0x2E22, and turns at 2 and 1
        // rather than 3 and 0 - so the tap is four frames against the toss's nine
        // and the two drift against each other, which is why it reads as a tap
        // rather than part of the throw.
        var a = Fresh();
        var seen = new List<int>();

        for (var i = 0; i < 12; i++) seen.Add(NextFrame(a).Foot);

        Assert.Equal([1, 2, 3, 2, 1, 0, 1, 2, 3, 2, 1, 0], seen);
    }

    // ---- the second set of expressions ------------------------------------
    //
    // The toss's frame sequence, which these all reason about:
    //
    //   index    0 1 2 3 4 5 6 7 8  9 10 11 12 13 14 15 16 17 18 19 20 21
    //   diamond  1 2 3 4 5 6 7 8 8  7  6  5  4  3  2  1  0  0  1  2  3  4
    //                  ^                    ^                           ^
    //                  up                   down                        up
    //
    // 0xF2ED fires only where the NEXT value is four, which is index 3 climbing,
    // index 12 descending and index 21 climbing again. Descending is the one that
    // can set the flag; the other two only clear it.
    private const int DescendingFour = 12;
    private const int NextClimbingFour = 21;

    [Fact]
    public void The_second_face_can_only_arrive_on_the_way_down()
    {
        // 0xF2F7 tests the direction before drawing, so nothing can set the flag
        // while the gem is rising. Every seed, not one - with a single seed the
        // draw may simply not have come up.
        for (ushort seed = 0; seed < 32; seed++)
        {
            var a = Fresh(seed);

            for (var i = 0; i <= DescendingFour - 1; i++)
                Assert.False(NextFrame(a).AlternateFace,
                             $"seed {seed} was alternate at frame {i}, before the turn");
        }
    }

    [Fact]
    public void The_second_face_is_handed_back_on_the_next_climb()
    {
        // 0xF2F2 clears before 0xF307 can set, and it clears climbing as well as
        // descending. So a toss that took the second face gives it up at the same
        // point next time round rather than keeping it for the rest of the screen.
        var a = Fresh(SeedThatAlternates());

        var frames = new List<bool>();
        for (var i = 0; i <= NextClimbingFour; i++) frames.Add(NextFrame(a).AlternateFace);

        Assert.True(frames[DescendingFour], "it should have taken the second face here");
        Assert.True(frames[DescendingFour + 1], "and kept it through the descent");
        Assert.False(frames[NextClimbingFour], "and given it back on the next climb");
    }

    [Fact]
    public void The_second_face_comes_up_about_one_toss_in_four()
    {
        // 0xF2FF draws from the game's own generator and 0xF302 tests the low two
        // bits, so it is one in four rather than never or always. Both of those
        // extremes look almost right on screen, which is why this counts.
        var took = 0;
        const int seeds = 200;

        for (ushort seed = 0; seed < seeds; seed++)
        {
            var a = Fresh(seed);
            var alternate = false;

            for (var i = 0; i <= DescendingFour; i++) alternate = NextFrame(a).AlternateFace;
            if (alternate) took++;
        }

        // A quarter of 200 is 50. The band is wide enough for the generator's own
        // bias and far too narrow for never, always, or one in two.
        Assert.InRange(took, 25, 75);
    }

    private static ushort SeedThatAlternates()
    {
        for (ushort seed = 0; seed < 64; seed++)
        {
            var a = Fresh(seed);
            var alternate = false;
            for (var i = 0; i <= DescendingFour; i++) alternate = NextFrame(a).AlternateFace;
            if (alternate) return seed;
        }

        throw new Xunit.Sdk.XunitException("no seed in 64 ever took the second face");
    }

    // ---- giving up --------------------------------------------------------

    [Fact]
    public void It_gives_up_after_twenty_seconds()
    {
        // 0xF366 compares the interrupt counter against 1200, which at 60 Hz is
        // twenty seconds, and the original then runs its attract panel again.
        var a = Fresh();

        Assert.False(a.Finished);

        for (var i = 0; i < TitleAnimation.TicksBeforeGivingUp - 1; i++) a.Tick();
        Assert.False(a.Finished);
        Assert.Equal(TitleAnimation.TicksBeforeGivingUp - 1, a.Ticks);

        a.Tick();
        Assert.True(a.Finished);
    }

    [Fact]
    public void Twenty_seconds_is_twelve_hundred_interrupts()
    {
        Assert.Equal(1200, TitleAnimation.TicksBeforeGivingUp);
        Assert.Equal(20, TitleAnimation.TicksBeforeGivingUp / 60);
    }
}
