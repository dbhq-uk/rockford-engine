using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// Dying, from the countdown at <c>0xEE76</c> and the cave-advance routine at
/// <c>0xFE03</c>.
///
/// Before this was ported the death timer was set to 8 and never decremented, so
/// the player died once and the cave sat there forever. Nothing failed, because
/// nothing asked.
/// </summary>
public class DeathAndLivesTests
{
    private static Simulation Dying()
    {
        var sim = new Simulation(new CaveGrid(), 600, randomSeed: 1);
        var centre = CaveGrid.AddressOf(5, 5);
        sim.PlayerCell = centre;
        sim.Blast(centre - Simulation.BlastCentreOffset, kind: 1);
        return sim;
    }

    [Fact]
    public void A_blast_on_the_player_starts_an_eight_frame_countdown()
    {
        var sim = Dying();

        Assert.True(sim.PlayerDead);
        Assert.Equal(8, sim.PlayerDeathTimer);
        Assert.False(sim.DeathComplete);
    }

    [Fact]
    public void The_countdown_runs_out_after_exactly_eight_steps()
    {
        var sim = Dying();

        for (var i = 0; i < 7; i++)
        {
            sim.Step();
            Assert.False(sim.DeathComplete);
            Assert.True(sim.PlayerDead);
        }

        sim.Step();

        Assert.True(sim.DeathComplete);
        Assert.False(sim.PlayerDead);
    }

    [Fact]
    public void Death_completing_is_one_pulse_and_not_a_latch()
    {
        var sim = Dying();
        for (var i = 0; i < 8; i++) sim.Step();
        Assert.True(sim.DeathComplete);

        sim.Step();

        Assert.False(sim.DeathComplete);
    }

    [Fact]
    public void The_countdown_clears_the_pending_sounds()
    {
        // 0xEE83. Without it the blast that killed the player is still queued when
        // the cave reloads, and speaks over the first frame of the new one.
        //
        // A HOLDING request, not a one-shot. This test used to queue Blast and
        // Collect, and the very first step's six interrupts served and cleared
        // both of them - so by the time the countdown finished there was nothing
        // left to clear, and deleting the clear entirely still passed. 0x80 never
        // clears itself, so it is still pending when the countdown lands.
        var sim = Dying();
        sim.Request(SoundEvent.Tick);

        for (var i = 0; i < 7; i++) sim.Step();
        Assert.Equal(0x80, sim.SoundFlags & 0x80);   // still raised, right up to the end

        sim.Step();

        Assert.Equal(0, sim.SoundFlags);
    }

    [Fact]
    public void Nothing_counts_down_when_the_player_is_alive()
    {
        // With a time on the clock. Left at the default of zero the clock runs out
        // after a second and kills the player, which is correct and is not what
        // this test is about.
        var sim = new Simulation(new CaveGrid(), 60, randomSeed: 1);

        for (var i = 0; i < 20; i++) sim.Step();

        Assert.False(sim.PlayerDead);
        Assert.False(sim.DeathComplete);
    }

    // ---- the session ----------------------------------------------------

    [Fact]
    public void A_game_starts_with_five_lives()
    {
        Assert.Equal(5, new GameSession().Lives);
    }

    [Fact]
    public void Dying_costs_a_life_and_replays_the_same_cave()
    {
        var game = new GameSession();
        game.Begin(world: 1, stage: 2);

        var outcome = game.Died();

        Assert.Equal(CaveOutcome.Died, outcome);
        Assert.Equal(4, game.Lives);
        Assert.Equal(2, game.Stage);      // the same cave, not the next one
        Assert.False(game.IsOver);
    }

    [Fact]
    public void The_fifth_death_is_game_over()
    {
        var game = new GameSession();
        game.Begin(0);

        for (var i = 0; i < 4; i++)
            Assert.Equal(CaveOutcome.Died, game.Died());

        Assert.Equal(CaveOutcome.GameOver, game.Died());
        Assert.Equal(0, game.Lives);
        Assert.True(game.IsOver);
    }

    [Fact]
    public void Restarting_after_game_over_resets_lives_score_and_stage()
    {
        // 0xFE26 to 0xFE38: the original resets and carries on rather than
        // returning to a menu.
        var game = new GameSession();
        game.Begin(world: 3, stage: 2);
        game.Award(4200);
        for (var i = 0; i < 5; i++) game.Died();

        game.Restart();

        Assert.Equal(5, game.Lives);
        Assert.Equal(0, game.Score);
        Assert.Equal(0, game.Stage);
        Assert.False(game.IsOver);
        Assert.Equal(3, game.World);      // the world you chose is kept
    }

    [Fact]
    public void Finishing_a_cave_advances_the_stage()
    {
        var game = new GameSession();
        game.Begin(world: 0, stage: 0);

        Assert.Equal(CaveOutcome.Completed, game.Completed());
        Assert.Equal(1, game.Stage);
    }

    [Fact]
    public void The_fourth_cave_finishes_the_world()
    {
        // 0xFE0C tests the stage against four. Finishing the fourth is not the
        // same event as finishing the first three, and the host should not have
        // to work that out from its own copy of the stage - which is how "all
        // twenty" used to fire on the fifth world even if you started in it.
        var game = new GameSession();
        game.Begin(world: 0, stage: 3);

        Assert.Equal(CaveOutcome.WorldCompleted, game.Completed());
        Assert.Equal(0, game.Stage);
    }

    [Fact]
    public void The_first_three_caves_only_finish_a_cave()
    {
        var game = new GameSession();
        game.Begin(world: 0, stage: 0);

        Assert.Equal(CaveOutcome.Completed, game.Completed());
        Assert.Equal(CaveOutcome.Completed, game.Completed());
        Assert.Equal(CaveOutcome.Completed, game.Completed());
        Assert.Equal(CaveOutcome.WorldCompleted, game.Completed());
    }

    [Fact]
    public void Finishing_a_world_keeps_the_score_and_the_lives()
    {
        // 0xFE1C tests lives and jumps straight to 0xFE38 while any remain,
        // skipping the score clears at 0xFE26 and 0xFE2C and the five-life reset
        // at 0xFE32. Only running out of lives clears them.
        var game = new GameSession();
        game.Begin(world: 0, stage: 3);
        game.Award(4_500);
        game.Died();                       // down to four lives

        game.Completed();

        Assert.Equal(4_500, game.Score);
        Assert.Equal(4, game.Lives);
        Assert.True(game.InProgress);      // and the game is still running
    }

    [Fact]
    public void A_game_is_in_progress_until_the_lives_run_out()
    {
        // This is what tells the world chooser whether it is starting a game or
        // continuing one. The original reaches that screen from both directions.
        var game = new GameSession();
        Assert.False(game.InProgress);

        game.Begin(0);
        Assert.True(game.InProgress);

        for (var i = 0; i < 4; i++) game.Died();
        Assert.True(game.InProgress);

        game.Died();
        Assert.False(game.InProgress);
    }

    [Fact]
    public void Finishing_a_cave_costs_nothing()
    {
        var game = new GameSession();
        game.Begin(0);

        game.Completed();

        Assert.Equal(5, game.Lives);
    }

    [Fact]
    public void A_bonus_life_adds_one()
    {
        // 0xF441, which is what puts "BONUS LIFE!" on the screen. There is no cap
        // in the original, so there is none here.
        var game = new GameSession();

        game.BonusLife();

        Assert.Equal(6, game.Lives);
    }
}
