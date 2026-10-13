using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// Scoring, from the award at <c>0x13C3E</c>, the threshold at <c>0x12238</c> and
/// the completion tally at <c>0xEE25</c>.
///
/// The score used to live on <see cref="Simulation"/> alone, which is one cave, so
/// every reload threw it away - dying or finishing a cave both started again from
/// zero. <see cref="GameSession.Score"/> existed and had no writer at all.
/// </summary>
public class ScoreTests
{
    [Fact]
    public void A_new_game_has_no_score()
    {
        Assert.Equal(0, new GameSession().Score);
    }

    [Fact]
    public void Points_accumulate()
    {
        var game = new GameSession();

        game.Award(10);
        game.Award(25);

        Assert.Equal(35, game.Score);
    }

    [Fact]
    public void The_score_survives_a_death()
    {
        var game = new GameSession();
        game.Begin(0);
        game.Award(500);

        game.Died();

        Assert.Equal(500, game.Score);
        Assert.Equal(4, game.Lives);
    }

    [Fact]
    public void The_score_survives_finishing_a_cave()
    {
        var game = new GameSession();
        game.Begin(0);
        game.Award(500);

        game.Completed();

        Assert.Equal(500, game.Score);
    }

    [Fact]
    public void Only_a_game_over_clears_it()
    {
        var game = new GameSession();
        game.Award(1234);

        game.Restart();

        Assert.Equal(0, game.Score);
    }

    // ---- the bonus life --------------------------------------------------

    [Fact]
    public void The_first_bonus_life_comes_at_twenty_thousand_not_ten()
    {
        // This is the detail that reading only `cmp ax,0x2710` gets wrong.
        // Passing ten thousand increments a carry count, and 0x12244 tests its
        // LOW BIT - an odd carry skips the award. So the first life lands on the
        // second carry.
        var game = new GameSession();

        game.Award(10_000);
        Assert.Equal(5, game.Lives);      // the first carry awards nothing

        game.Award(10_000);
        Assert.Equal(6, game.Lives);      // the second does
    }

    [Fact]
    public void Bonus_lives_keep_coming_every_twenty_thousand()
    {
        var game = new GameSession();
        var at = new List<int>();

        // One point at a time, which is how the original does it, so nothing here
        // depends on the batching loop being right.
        for (var i = 1; i <= 100_000; i++)
        {
            game.Award(1);
            if (game.BonusJustAwarded) at.Add(i);
        }

        Assert.Equal([20_000, 40_000, 60_000, 80_000, 100_000], at);
    }

    [Fact]
    public void A_batched_award_does_not_lose_a_carry()
    {
        // The original is called after every single point, so it never has more
        // than one carry to make. A host awarding in batches could, and an `if`
        // where the original has a loop would silently drop a life.
        var game = new GameSession();

        game.Award(45_000);

        Assert.Equal(45_000, game.Score);
        Assert.Equal(7, game.Lives);      // two bonuses, at 20,000 and 40,000
    }

    [Fact]
    public void The_bonus_flag_is_one_pulse()
    {
        var game = new GameSession();
        game.Award(20_000);
        Assert.True(game.BonusJustAwarded);

        game.Award(1);

        Assert.False(game.BonusJustAwarded);
    }

    [Fact]
    public void Restarting_clears_the_carry_count_as_well_as_the_points()
    {
        // 0xFE26 and 0xFE2C clear both words. Clearing only the low one would
        // leave the carry count behind and hand the next game a bonus life it
        // had not earned.
        var game = new GameSession();
        game.Award(10_000);      // one carry, no bonus yet

        game.Restart();
        game.Award(10_000);      // should be the FIRST carry again

        Assert.Equal(5, game.Lives);
    }

    // ---- the simulation side ---------------------------------------------

    private static Simulation Sim() =>
        new(new CaveGrid(), 600, randomSeed: 1);

    [Fact]
    public void The_delta_is_cleared_when_it_is_taken()
    {
        var sim = Sim();
        sim.TimeRemaining = 40;

        var first = sim.CashInTime();
        var second = sim.TakeScoreDelta();
        var third = sim.TakeScoreDelta();

        Assert.Equal(40, first);
        Assert.Equal(40, second);
        Assert.Equal(0, third);
    }

    [Fact]
    public void Finishing_with_time_left_turns_it_into_points()
    {
        // 0xEE25: one point a second, and the clock runs to nothing.
        var sim = Sim();
        sim.TimeRemaining = 75;

        var earned = sim.CashInTime();

        Assert.Equal(75, earned);
        Assert.Equal(0, sim.TimeRemaining);
        Assert.Equal(75, sim.Score);
    }

    [Fact]
    public void Finishing_with_no_time_left_awards_nothing()
    {
        var sim = Sim();
        sim.TimeRemaining = 0;

        Assert.Equal(0, sim.CashInTime());
    }

    [Fact]
    public void The_completion_award_can_earn_a_bonus_life()
    {
        // Astra's point about ordering: the threshold must be checked as the
        // points are awarded, not after. A cave finished on 19,950 with 60
        // seconds left has earned a life.
        var game = new GameSession();
        game.Award(19_950);
        Assert.Equal(5, game.Lives);

        var sim = Sim();
        sim.TimeRemaining = 60;
        game.Award(sim.CashInTime());

        Assert.Equal(6, game.Lives);
        Assert.Equal(20_010, game.Score);
    }
}
