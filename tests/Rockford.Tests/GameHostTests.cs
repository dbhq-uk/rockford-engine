using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The host loop - the part of the game that is not one cave, from the main loop at
/// <c>0xEDA5</c> and the cave-advance routine at <c>0xFE03</c>.
///
/// This is issue #24. All of it used to be inside the browser component, tangled with
/// the canvas and the speaker, and none of it could be tested; it is in the engine now
/// and none of it needs a browser. What does is a <see cref="PlayEffects"/> the host
/// hands back for the shell to carry out, and these tests read that record.
///
/// The caves here are synthetic - eight of them, built in code - so the tests run on a
/// clean checkout. The real files are copyright and CI does not have them.
/// </summary>
public class GameHostTests
{
    private const int Worlds = 2;
    private const int Records = Worlds * GameHost.CavesPerWorld;

    /// <summary>
    /// A host over synthetic caves. Each is walled in steel with a player, an exit,
    /// and <paramref name="treasure"/> gems in a row on the floor.
    /// </summary>
    private static GameHost Host(int treasure = 2, int seconds = 100, int playerX = 2,
                                 WorldEntry[]? worlds = null)
    {
        worlds ??=
        [
            new() { Sheet = "one", World = 0 },
            new() { Sheet = "two", World = 1 },
        ];

        // Enough records for the highest raw world named, because a menu that maps
        // to raw worlds 1, 3 and 9 needs records that far along.
        var records = Math.Max(Records, (worlds.Max(w => w.World) + 1) * GameHost.CavesPerWorld);
        var cellmaps = new byte[records * CaveLoader.RecordSize];

        for (var record = 0; record < records; record++)
        {
            var at = record * CaveLoader.RecordSize;

            for (var y = 0; y < CaveLoader.AuthoredHeight; y++)
            for (var x = 0; x < CaveLoader.AuthoredWidth; x++)
            {
                var edge = x == 0 || y == 0
                    || x == CaveLoader.AuthoredWidth - 1
                    || y == CaveLoader.AuthoredHeight - 1;

                cellmaps[at + y * CaveLoader.AuthoredWidth + x] = edge ? (byte)0x02 : (byte)0x00;
            }

            // A player with the gems immediately to his right, so walking right
            // collects them, and the exit well clear of both.
            var row = at + 20 * CaveLoader.AuthoredWidth;
            cellmaps[row + playerX] = 0x20;                 // the player
            for (var i = 0; i < treasure; i++)
                cellmaps[row + playerX + 1 + i] = 0x88;     // treasure
            cellmaps[row + 34] = 0x74;                      // the closed exit
        }

        var tables = new CaveTables
        {
            Required = Fill(treasure),
            Time = Fill(seconds),
            ScoreBefore = Fill(10),
            ScoreAfter = Fill(20),
            AmoebaCap = Fill(200),
        };

        return new GameHost(cellmaps, tables, worlds);
    }

    private static int[] Fill(int value) => [.. Enumerable.Repeat(value, 64)];

    private static PlayerAction Still => new(0, 0, Fire: false);

    /// <summary>
    /// A host part-way through a game, the way the chooser leaves it: a cave up, a
    /// session begun at the right stage, and the screen on Playing.
    /// </summary>
    private static GameHost Playing(int world = 0, int stage = 0, int treasure = 2,
                                    int seconds = 100, int playerX = 2)
    {
        var host = Host(treasure, seconds, playerX);
        host.Selected = world;
        host.StartSelectedWorld();

        if (stage == 0) return host;

        host.Session.Begin(world, stage);
        host.Load(world, stage);
        return host;
    }

    // ---- restarting and abandoning ----------------------------------------

    [Fact]
    public void Restarting_a_cave_costs_a_life()
    {
        // It used to be free, which is the whole reason this exists: collect the
        // treasure, restart, collect it again, and the score climbs with nothing
        // spent. That sat badly beside the release cave-stepper, which came out
        // for removing the stakes.
        var host = Playing();
        var lives = host.Session.Lives;

        Assert.True(host.RestartCave());
        Assert.Equal(lives - 1, host.Session.Lives);
    }

    [Fact]
    public void Restarting_reloads_the_same_cave_rather_than_advancing()
    {
        var host = Playing(world: 1, stage: 2);

        Assert.True(host.RestartCave());

        Assert.Equal(1, host.World);
        Assert.Equal(2, host.Stage);
        Assert.Equal(GameScreen.Playing, host.Screen);
        Assert.NotNull(host.Sim);
    }

    [Fact]
    public void Restarting_on_the_last_life_ends_the_game()
    {
        var host = Playing();
        while (host.Session.Lives > 1) host.Session.Died();

        Assert.True(host.RestartCave());

        Assert.True(host.Session.IsOver);
        Assert.Equal("game over", host.Message);
        Assert.True(host.HoldUntil > 0);
    }

    [Fact]
    public void Restarting_is_refused_during_a_hold()
    {
        // A death countdown or a completion hold is a cave mid-transition:
        // Session.Stage has already advanced past a completed cave while
        // GameHost.Stage still names it. Restarting into that gap reloads the
        // finished cave with the two disagreeing, so the gap is closed by making
        // it unreachable rather than by reconciling two fields afterwards.
        var host = Playing(treasure: 0);

        host.Tick(new PlayerAction(PlayerAction.Right, 0, Fire: false));
        while (host.HoldUntil == 0 && host.Message is null)
            host.Tick(new PlayerAction(PlayerAction.Right, 0, Fire: false));

        Assert.True(host.HoldUntil > 0, "the cave never reached a hold");

        var lives = host.Session.Lives;
        var stage = host.Stage;

        Assert.False(host.RestartCave());
        Assert.Equal(lives, host.Session.Lives);
        Assert.Equal(stage, host.Stage);
    }

    [Fact]
    public void Restarting_is_refused_while_the_player_is_exploding()
    {
        // The eight-frame death countdown is NOT a hold. It lives in the
        // simulation and the host does not raise HoldUntil until DeathComplete
        // fires at the end of it, so a guard on HoldUntil alone accepted a restart
        // through the whole explosion - which is the thing it was meant to refuse.
        var host = Playing();
        host.Sim!.Blast(host.Sim.PlayerCell - Simulation.BlastCentreOffset, kind: 2);

        host.Tick(Still);
        Assert.True(host.Sim!.PlayerDead, "the player is not dying");
        Assert.Equal(0, host.HoldUntil);

        var lives = host.Session.Lives;
        Assert.False(host.RestartCave());
        Assert.False(host.AbandonToChooser());
        Assert.Equal(lives, host.Session.Lives);
    }

    [Fact]
    public void The_game_that_replaces_a_game_over_is_marked_in_progress()
    {
        // CountDownHold reset the session and loaded a cave but never began it, so
        // the replacement game ran with InProgress false. Finish a world in it and
        // the chooser - which only preserves a run that is in progress - wiped the
        // score and lives it had just been played for.
        var host = Playing();
        while (host.Session.Lives > 1) host.Session.Died();

        Assert.True(host.RestartCave());
        Assert.True(host.Session.IsOver);

        for (var i = 0; i < GameHost.GameOverHold + 2; i++) host.Tick(Still);

        Assert.False(host.Session.IsOver);
        Assert.True(host.Session.InProgress,
            "the replacement game is not marked in progress");

        // And the consequence: a score earned in it survives the chooser.
        host.Session.Award(500);
        host.Selected = 1;
        host.StartSelectedWorld();
        Assert.Equal(500, host.Session.Score);
    }

    [Fact]
    public void Restarting_is_refused_when_there_is_no_cave()
    {
        var host = Host();
        Assert.Equal(GameScreen.Title, host.Screen);
        Assert.False(host.RestartCave());
    }

    [Fact]
    public void Choosing_a_world_mid_cave_abandons_the_run()
    {
        // The original only reaches the chooser at the start of a game or after a
        // world is finished, and the second of those deliberately keeps the score
        // and the lives. Walking out of a cave part-way through is a route it does
        // not have, so carrying a score through it would be a new rule invented by
        // accident rather than anything ported.
        //
        // Note this is NOT the same as saying the chooser always starts a fresh
        // game. Reached by finishing a world it continues the run, which is
        // 0xFE1C's own behaviour and is covered by the world-completion tests. Two
        // doors, not one door behaving inconsistently.
        var host = Playing();
        host.Session.Award(500);
        host.Session.Died();

        Assert.True(host.AbandonToChooser());

        Assert.Equal(GameScreen.Menu, host.Screen);
        Assert.False(host.Session.InProgress);
        Assert.Equal(0, host.Session.Score);
        Assert.Equal(GameSession.StartingLives, host.Session.Lives);
    }

    [Fact]
    public void And_the_world_chosen_after_abandoning_starts_a_fresh_game()
    {
        var host = Playing();
        host.Session.Award(500);
        host.AbandonToChooser();

        host.Selected = 1;
        host.StartSelectedWorld();

        Assert.Equal(0, host.Session.Score);
        Assert.Equal(GameSession.StartingLives, host.Session.Lives);
        Assert.Equal(0, host.Stage);
    }

    [Fact]
    public void Abandoning_is_refused_during_a_hold()
    {
        var host = Playing(treasure: 0);

        host.Tick(new PlayerAction(PlayerAction.Right, 0, Fire: false));
        while (host.HoldUntil == 0 && host.Message is null)
            host.Tick(new PlayerAction(PlayerAction.Right, 0, Fire: false));

        Assert.True(host.HoldUntil > 0, "the cave never reached a hold");
        Assert.False(host.AbandonToChooser());
        Assert.Equal(GameScreen.Playing, host.Screen);
    }

    // ---- observations -----------------------------------------------------

    [Fact]
    public void A_tick_hands_on_what_the_step_did_and_drains_it()
    {
        // The read-only observation surface reaches a front end through here, and
        // the host must take it every tick. PlayerTick runs before Step and both
        // write into the same buffer, so this is also the check that the player's
        // own move is not lost to the scan that follows it.
        var host = Playing();

        var effects = host.Tick(new PlayerAction(PlayerAction.Right, 0, Fire: false));

        Assert.Contains(effects.Observations,
            o => o.Kind == ObservationKind.Moved && o.Value == RuntimeTile.Player);

        // Taken, so the simulation is not still holding a step that has gone.
        Assert.Empty(host.Sim!.TakeObservations());
    }

    [Fact]
    public void A_tick_with_no_cave_reports_an_empty_list_rather_than_null()
    {
        // Observations has to be a list on every path out of Tick, including the
        // ones that do nothing at all, or every caller needs a null guard for a
        // case that never carries information anyway.
        var host = Host();
        Assert.Null(host.Sim);

        Assert.Empty(host.Tick(Still).Observations);
        Assert.Empty(PlayEffects.Nothing.Observations);
    }

    // ---- loading a cave ---------------------------------------------------

    [Fact]
    public void A_host_starts_on_the_title_with_no_cave()
    {
        var host = Host();

        Assert.Equal(GameScreen.Title, host.Screen);
        Assert.Null(host.Sim);
        Assert.Equal(GameSession.StartingLives, host.Session.Lives);
    }

    [Fact]
    public void Loading_a_cave_sets_the_quota_the_clock_and_the_player()
    {
        var host = Host(treasure: 3, seconds: 90);

        host.Load(0, 0);

        Assert.NotNull(host.Sim);
        Assert.Equal(3, host.Needed);
        Assert.Equal(0, host.Collected);
        Assert.Equal(90, host.Time);
        Assert.False(host.ExitOpen);
        Assert.Null(host.Message);
        Assert.Equal(0, host.HoldUntil);
        Assert.Equal(CaveGrid.AddressOf(2, 20), host.Sim!.PlayerCell);
    }

    [Fact]
    public void Loading_a_cave_shows_its_clock_and_not_the_last_one_s()
    {
        // The one deliberate behaviour change in the extraction. The component
        // never set the clock on load, so the status row kept the previous cave's
        // seconds for 100 ms - and on a fresh game that was one frame of a red
        // "time 0", because zero is under the warning threshold.
        var host = Host(seconds: 80);
        host.Load(0, 0);

        for (var i = 0; i < 30; i++) host.Tick(Still);
        Assert.True(host.Time < 80);

        host.Load(0, 1);

        Assert.Equal(80, host.Time);
    }

    [Fact]
    public void The_record_is_the_raw_world_number_times_four_plus_the_stage()
    {
        // The menu's entries map to RAW world numbers, which is what indexes the
        // records - so the second menu world is records 4 to 7 here because its
        // raw number is 1. In the shipped game the raw numbers are 0, 2, 4, 6 and
        // 8, which is why the playable records are 0-3, 8-11 and so on.
        var host = Host();

        host.Load(0, 2);
        Assert.Equal(2, host.Record);

        host.Load(1, 3);
        Assert.Equal(7, host.Record);
    }

    [Fact]
    public void Stepping_a_cave_rolls_into_the_next_world_and_stops_at_both_ends()
    {
        var host = Host();
        host.Load(0, 3);

        host.Go(1);
        Assert.Equal(1, host.World);
        Assert.Equal(0, host.Stage);

        host.Go(-1);
        Assert.Equal(0, host.World);
        Assert.Equal(3, host.Stage);

        // Clamped rather than wrapped, at both ends.
        host.Load(0, 0);
        host.Go(-1);
        Assert.Equal(0, host.World);
        Assert.Equal(0, host.Stage);

        host.Load(1, 3);
        Assert.True(host.AtLastCave);
        host.Go(1);
        Assert.Equal(1, host.World);
        Assert.Equal(3, host.Stage);
    }

    [Fact]
    public void A_new_cave_carries_the_globals_the_loader_does_not_clear()
    {
        // The footstep's cell and pitch, the timed wall's timestamp and both worms'
        // headings. A Simulation is one cave; these are not, and the host is what
        // hands them over. Without it every cave starts in a state the original
        // never reaches.
        var host = Host();
        host.Load(0, 0);

        host.Sim!.FootstepToggle = true;
        host.Sim.LastSoundedCell = 0x1234;
        host.Sim.TimedWallStart = 999;
        host.Sim.WormADirection = Simulation.DirDown;

        host.Load(0, 1);

        Assert.True(host.Sim!.FootstepToggle);
        Assert.Equal(0x1234, host.Sim.LastSoundedCell);
        Assert.Equal(999, host.Sim.TimedWallStart);
        Assert.Equal(Simulation.DirDown, host.Sim.WormADirection);
    }

    // ---- a tick of play ---------------------------------------------------

    [Fact]
    public void A_tick_with_no_cave_does_nothing()
    {
        var host = Host();

        var fx = host.Tick(Still);

        Assert.False(fx.Stepped);
        Assert.Empty(fx.Sounds);
    }

    [Fact]
    public void A_tick_runs_the_cave_and_reports_its_sound()
    {
        var host = Host();
        host.Load(0, 0);

        var fx = host.Tick(Still);

        Assert.True(fx.Stepped);
        Assert.Equal(Sting.None, fx.Sting);
        Assert.False(fx.SheetChanged);

        // Six frames, because the driver runs on a 60 Hz interrupt while a step is
        // one pass of the 10 Hz loop. A host that reported one would be throwing
        // five sixths of the game's sound away.
        Assert.Equal(Simulation.TicksPerStep, fx.Sounds.Count);
        Assert.Equal(1, host.Frame);
    }

    [Fact]
    public void The_status_row_follows_the_cave()
    {
        var host = Host(treasure: 2);
        host.Load(0, 0);

        host.Tick(Still);
        Assert.Equal(0, host.Collected);
        Assert.False(host.ExitOpen);

        // Hand the cave its quota directly: this is about the projection rather
        // than about the player, who has his own tests.
        host.Sim!.TreasureQuota = 0;
        host.Tick(Still);

        Assert.Equal(2, host.Collected);
        Assert.True(host.ExitOpen);
    }

    // ---- finishing a cave -------------------------------------------------

    [Fact]
    public void Finishing_one_of_the_first_three_caves_holds_and_plays_the_cave_sting()
    {
        var host = Playing();

        host.Sim!.CaveComplete = true;
        var fx = host.Tick(Still);

        Assert.Equal(Sting.Cave, fx.Sting);
        Assert.False(fx.ClearTaps);
        Assert.Equal("out", host.Message);
        Assert.Equal(GameHost.CaveHold, host.HoldUntil);
        Assert.Equal(GameScreen.Playing, host.Screen);
    }

    [Fact]
    public void Finishing_the_fourth_opens_the_interlude_and_clears_the_taps()
    {
        // Gameplay reads held keys rather than taps, so every movement key pressed
        // during the cave is still in the tap set. A press-anything screen opening
        // on that backlog dismisses itself on its first tick.
        var host = Playing(stage: 3);

        host.Sim!.CaveComplete = true;
        var fx = host.Tick(Still);

        Assert.Equal(Sting.World, fx.Sting);
        Assert.True(fx.ClearTaps);
        Assert.Equal("world complete", host.Message);
        Assert.Equal(GameScreen.Interlude, host.Screen);

        // No hold: the screen waits for the player rather than counting down.
        Assert.Equal(0, host.HoldUntil);
    }

    [Fact]
    public void The_time_left_becomes_points_once_and_only_once()
    {
        // 0xEE25: a second left is a point. The guard is that Message is already
        // set on the ticks after the first, so a cave sitting on its hold does not
        // cash the clock in again every step.
        var host = Playing(seconds: 50);

        host.Sim!.CaveComplete = true;
        host.Tick(Still);

        var afterFirst = host.Session.Score;
        Assert.True(afterFirst >= 50, $"the clock should have paid out, got {afterFirst}");
        Assert.Equal(0, host.Time);

        // The hold counts down without paying again.
        host.Tick(Still);
        Assert.Equal(afterFirst, host.Session.Score);
    }

    [Fact]
    public void The_hold_runs_down_and_then_loads_the_next_cave()
    {
        var host = Playing();

        host.Sim!.CaveComplete = true;
        host.Tick(Still);
        Assert.Equal(GameHost.CaveHold, host.HoldUntil);

        // Every tick but the last only decrements, and produces no sound at all -
        // the cave is frozen on its last frame.
        for (var i = 0; i < GameHost.CaveHold - 1; i++)
        {
            var held = host.Tick(Still);
            Assert.False(held.Stepped);
            Assert.Empty(held.Sounds);
            Assert.False(held.SheetChanged);
            Assert.Equal(0, host.Stage);
        }

        var last = host.Tick(Still);

        Assert.True(last.SheetChanged);
        Assert.Equal(1, host.Stage);
        Assert.Null(host.Message);
        Assert.Equal(0, host.HoldUntil);
    }

    // ---- dying ------------------------------------------------------------

    [Fact]
    public void A_death_takes_a_life_when_the_countdown_finishes_and_not_before()
    {
        // The life goes on DeathComplete, not on PlayerDead - the latter is true
        // for the whole eight-frame explosion, and taking the life on the first
        // frame of it would end the cave before the player saw why.
        var host = Playing();
        var lives = host.Session.Lives;

        // Blow the player up. Eight frames of countdown follow.
        host.Sim!.Blast(host.Sim.PlayerCell - Simulation.BlastCentreOffset, kind: 2);

        var guard = 0;
        while (host.Message is null && guard++ < 20)
        {
            host.Tick(Still);
            if (host.Message is null) Assert.Equal(lives, host.Session.Lives);
        }

        Assert.Equal("caught", host.Message);
        Assert.Equal(lives - 1, host.Session.Lives);
        Assert.Equal(GameHost.CaveHold, host.HoldUntil);
    }

    [Fact]
    public void A_death_reloads_the_same_cave_rather_than_advancing()
    {
        var host = Playing(stage: 2);

        host.Sim!.Blast(host.Sim.PlayerCell - Simulation.BlastCentreOffset, kind: 2);

        var guard = 0;
        while (host.HoldUntil == 0 && guard++ < 20) host.Tick(Still);
        while (host.HoldUntil > 0 && guard++ < 60) host.Tick(Still);

        Assert.Equal(2, host.Stage);
        Assert.NotNull(host.Sim);
        Assert.Equal(CaveGrid.AddressOf(2, 20), host.Sim!.PlayerCell);
    }

    [Fact]
    public void The_last_life_is_a_game_over_with_a_longer_hold_and_a_fresh_game()
    {
        // 0xFE23: the original does not return to a menu. It resets and carries on.
        var host = Playing();

        while (host.Session.Lives > 1) host.Session.Died();
        host.Session.Award(500);

        host.Sim!.Blast(host.Sim.PlayerCell - Simulation.BlastCentreOffset, kind: 2);

        var guard = 0;
        while (host.HoldUntil == 0 && guard++ < 20) host.Tick(Still);

        Assert.Equal("game over", host.Message);
        Assert.Equal(GameHost.GameOverHold, host.HoldUntil);
        Assert.True(host.Session.IsOver);

        while (host.HoldUntil > 0 && guard++ < 80) host.Tick(Still);

        Assert.False(host.Session.IsOver);
        Assert.Equal(GameSession.StartingLives, host.Session.Lives);
        Assert.Equal(0, host.Session.Score);
        Assert.Equal(0, host.Stage);
    }

    // ---- the chooser ------------------------------------------------------

    [Fact]
    public void Choosing_a_world_starts_a_new_game_only_when_none_is_running()
    {
        // 0xFE1C tests lives and jumps past the score clears while any remain, so
        // coming back to the chooser after finishing a world keeps what you had.
        var host = Host();
        host.Selected = 1;
        host.StartSelectedWorld();

        Assert.Equal(GameScreen.Playing, host.Screen);
        Assert.Equal(1, host.World);
        Assert.Equal(0, host.Stage);

        host.Session.Award(750);
        host.ToMenu();

        host.Selected = 0;
        host.StartSelectedWorld();

        Assert.Equal(750, host.Session.Score);
        Assert.Equal(0, host.World);
    }

    [Fact]
    public void Choosing_a_world_after_a_game_over_starts_from_nothing()
    {
        var host = Host();
        host.Selected = 0;
        host.StartSelectedWorld();
        host.Session.Award(750);

        while (host.Session.Died() != CaveOutcome.GameOver) { }
        Assert.True(host.Session.IsOver);

        host.ToMenu();
        host.StartSelectedWorld();

        Assert.Equal(0, host.Session.Score);
        Assert.Equal(GameSession.StartingLives, host.Session.Lives);
    }

    [Fact]
    public void The_highlight_clamps_and_says_whether_it_moved()
    {
        // The shell only repaints the chooser when it actually moved, so a tap at
        // the first world has to report false rather than repainting on every
        // press against the end.
        var host = Host();

        Assert.False(host.MoveSelection(-1));
        Assert.Equal(0, host.Selected);

        Assert.True(host.MoveSelection(+1));
        Assert.Equal(1, host.Selected);

        Assert.False(host.MoveSelection(+1));
        Assert.Equal(Worlds - 1, host.Selected);
    }

    [Fact]
    public void The_menu_clears_whatever_the_last_cave_said()
    {
        var host = Playing();
        host.Sim!.CaveComplete = true;
        host.Tick(Still);
        Assert.NotNull(host.Message);

        host.ToMenu();

        Assert.Equal(GameScreen.Menu, host.Screen);
        Assert.Null(host.Message);
    }

    // ---- the gaps mutation testing found ----------------------------------

    [Fact]
    public void Collecting_treasure_raises_the_session_score()
    {
        // The cave reports what it just awarded and the session keeps the running
        // total, because the score outlives the cave. Drop the drain and every
        // point a player earns disappears at the end of the step that earned it.
        var host = Playing(treasure: 2);
        Assert.Equal(0, host.Session.Score);

        var right = new PlayerAction(PlayerAction.Right, 0, Fire: false);

        var guard = 0;
        while (host.Collected == 0 && guard++ < 10) host.Tick(right);

        Assert.True(host.Collected > 0, "the player never reached the treasure");
        Assert.True(host.Session.Score > 0, "the points never reached the session");
    }

    [Fact]
    public void A_finished_world_does_not_cash_its_clock_in_twice()
    {
        // The guard is Message already being set. Without it a cave sitting on the
        // interlude would pay out its remaining seconds on every tick - and the
        // world screen has no hold counting down to stop it.
        var host = Playing(stage: 3, seconds: 50);

        host.Sim!.CaveComplete = true;
        host.Tick(Still);

        var once = host.Session.Score;
        Assert.True(once >= 50);

        // The clock itself is already zero, so a second cash-in would pay nothing
        // and the score alone cannot see the guard. What it can see is the rest of
        // the block running again: the sting re-firing, and Completed() moving the
        // session's stage a second time.
        var again = host.Tick(Still);

        Assert.Equal(once, host.Session.Score);
        Assert.Equal(Sting.None, again.Sting);
        Assert.False(again.ClearTaps);
    }

    [Fact]
    public void The_explosion_plays_out_before_the_life_is_taken()
    {
        // The life goes on DeathComplete, not PlayerDead. The latter is true for
        // the whole eight-frame countdown, so taking the life on the first frame
        // would end the cave before the player saw what killed them.
        var host = Playing();
        var lives = host.Session.Lives;

        host.Sim!.Blast(host.Sim.PlayerCell - Simulation.BlastCentreOffset, kind: 2);

        var ticks = 0;
        while (host.Message is null && ticks < 20) { host.Tick(Still); ticks++; }

        // Eight frames, from the countdown 0x11384 writes and 0xEE76 decrements.
        Assert.Equal(8, ticks);
        Assert.Equal(lives - 1, host.Session.Lives);
    }

    [Fact]
    public void A_finished_cave_takes_twelve_ticks_to_become_the_next_one()
    {
        // The literal, not the constant. A test written against GameHost.CaveHold
        // moves with it, so shortening the hold to eleven passes - and the hold is
        // a number chosen against the sting's own length.
        var host = Playing();

        host.Sim!.CaveComplete = true;
        host.Tick(Still);
        Assert.Equal(12, host.HoldUntil);

        for (var i = 0; i < 11; i++)
        {
            host.Tick(Still);
            Assert.Equal(0, host.Stage);
        }

        host.Tick(Still);
        Assert.Equal(1, host.Stage);
    }

    [Fact]
    public void The_session_is_begun_on_the_world_that_was_chosen()
    {
        // Two places track the world and they must not drift. The host's own World
        // comes from the Load; the session's comes from Begin, and it is what
        // decides whether the fourth cave finishes a world.
        var host = Host();
        host.Selected = 1;

        host.StartSelectedWorld();

        Assert.Equal(1, host.World);
        Assert.Equal(1, host.Session.World);
    }

    [Fact]
    public void Loading_a_cave_clears_whatever_the_last_one_said()
    {
        var host = Playing();
        host.Sim!.CaveComplete = true;
        host.Tick(Still);
        Assert.NotNull(host.Message);

        host.Load(0, 1);

        Assert.Null(host.Message);
    }

    [Fact]
    public void The_pose_comes_from_the_cave_and_not_from_the_host()
    {
        // The engine owns the choice, because picking between the two idle poses is
        // a draw from the game's own generator - deciding it out here would move
        // the whole random stream. The host only carries it to the renderer.
        var host = Playing();
        var right = new PlayerAction(PlayerAction.Right, 0, Fire: false);

        host.Tick(right);

        Assert.Equal(host.Sim!.PlayerPose, host.Pose);
        Assert.NotEqual(0, host.Pose);
    }

    [Fact]
    public void The_camera_does_not_follow_a_shot()
    {
        // 0xEDBC calls the camera on the walking path only. Follow is not
        // idempotent - it nudges the origin one cell per call while a latch is on -
        // so following on a fire tick too would scroll the cave out from under a
        // player who was standing still and shooting.
        var host = Playing(treasure: 0, playerX: 5);
        var right = new PlayerAction(PlayerAction.Right, 0, Fire: false);

        // Walk until the camera starts moving, which is what turns the latch on.
        var before = host.View.Column;
        var guard = 0;
        while (host.View.Column == before && guard++ < 25) host.Tick(right);
        Assert.True(host.View.Column != before, "the camera never started scrolling");

        // Now stand still and shoot. The latch is on, so an extra Follow would
        // move the origin again.
        var parked = host.View.Column;
        host.Tick(new PlayerAction(PlayerAction.Right, 0, Fire: true));

        Assert.Equal(parked, host.View.Column);
    }

    // ---- what the status row is given -------------------------------------

    [Fact]
    public void The_status_row_is_built_here_and_not_in_the_markup()
    {
        // Eleven bindings in the page were eleven lines that could name the wrong
        // property with nothing to catch it - which is how the lives field came to
        // show the total rather than the spares. They are one value now, built
        // here, where each choice has a test.
        var host = Playing(treasure: 4, seconds: 77);
        host.Session.Award(450);

        var row = host.Status();

        Assert.True(row.Playing);
        Assert.Equal("one", row.Sheet);
        Assert.Equal(0, row.Stage);
        Assert.Equal("GOLD", row.TreasureName);
        Assert.Equal(GameSession.StartingLives - 1, row.SpareLives);  // spares
        Assert.Equal(0, row.Collected);
        Assert.Equal(4, row.Needed);
        Assert.Equal("000450", row.Score);                            // six digits
        Assert.Equal(77, row.Time);
        Assert.False(row.ExitOpen);
        Assert.Null(row.Message);
    }

    [Fact]
    public void The_status_row_follows_the_cave_into_its_other_states()
    {
        // The other half. One sample only ever shows the fields that happen to be
        // false or null in it, so a Status that hard-coded "shut", "no message" or
        // "cave one" would pass the test above untouched.
        var host = Playing(stage: 2, treasure: 2);

        host.Sim!.TreasureQuota = 0;
        host.Sim.CaveComplete = true;
        host.Tick(Still);

        var row = host.Status();

        Assert.Equal(2, row.Stage);
        Assert.True(row.ExitOpen);
        Assert.Equal("out", row.Message);
        Assert.Equal(2, row.Collected);
    }

    [Fact]
    public void The_status_row_is_not_playing_before_a_world_is_chosen()
    {
        var row = Host().Status();

        Assert.False(row.Playing);
    }

    [Fact]
    public void Each_world_names_its_own_treasure()
    {
        // All ten fields at ds:0x1C81, in raw world order.
        Assert.Equal(
            ["GOLD", "GEMS", "APPLES", "CUPS", "COINS", "NOTES", "SUNS", "CLUBS", "HEARTS", "JEWELS"],
            GameHost.TreasureNames);

        var host = Playing(world: 1);
        Assert.Equal("GEMS", host.Status().TreasureName);
    }

    // ---- ten worlds, two pages (#44) ----------------------------------------

    /// <summary>The shipped menu order, 0 2 4 6 8 1 3 5 7 9, as far as it goes.</summary>
    private static WorldEntry[] Menu(params int[] raw) =>
        [.. raw.Select(w => new WorldEntry { Sheet = $"raw{w}", World = w })];

    [Fact]
    public void The_treasure_follows_the_raw_world_not_the_menu_position()
    {
        // The table used to be five long and indexed by MENU position, which only
        // worked while position and name happened to line up. Here they do not:
        // position 1 is raw world 2, so it collects apples, and position 5 is raw
        // world 1, so it collects gems - the sixth name of a five-name table.
        var host = Host(worlds: Menu(0, 2, 4, 6, 8, 1, 3));

        host.Selected = 1;
        host.StartSelectedWorld();
        Assert.Equal("APPLES", host.Status().TreasureName);

        host.Load(5, 0);
        Assert.Equal("GEMS", host.Status().TreasureName);

        host.Load(6, 0);
        Assert.Equal("CUPS", host.Status().TreasureName);
    }

    [Fact]
    public void The_second_page_plays_the_raw_worlds_the_1988_menu_skips()
    {
        // Position 5 is raw world 1, so its caves are records 4 to 7 - scuba's,
        // which shipped in 1987 as the second world and sit between hunter and
        // cook in the cave file.
        var host = Host(worlds: Menu(0, 2, 4, 6, 8, 1, 3, 5, 7, 9));

        host.Load(5, 0);
        Assert.Equal(4, host.Record);

        host.Load(6, 3);
        Assert.Equal(15, host.Record);

        host.Load(9, 3);
        Assert.Equal(39, host.Record);
    }

    [Fact]
    public void One_page_stops_at_its_ends_as_1988_does()
    {
        var host = Host(worlds: Menu(0, 2, 4, 6, 8));

        Assert.False(host.MoveSelection(-1));
        Assert.Equal(0, host.Selected);

        host.Selected = 4;
        Assert.False(host.MoveSelection(+1));
        Assert.Equal(4, host.Selected);
    }

    [Fact]
    public void Two_pages_wrap_so_the_second_has_a_way_in_from_either_end()
    {
        var host = Host(worlds: Menu(0, 2, 4, 6, 8, 1, 3));

        // Right off the fifth box is the first world of the second page.
        host.Selected = 4;
        Assert.True(host.MoveSelection(+1));
        Assert.Equal(5, host.Selected);
        Assert.Equal(1, WorldList.PageOf(host.Selected));
        Assert.Equal(0, WorldList.BoxOf(host.Selected));

        // Left off the first box is the last world offered.
        host.Selected = 0;
        Assert.True(host.MoveSelection(-1));
        Assert.Equal(6, host.Selected);

        // And right off that comes back round to the start.
        Assert.True(host.MoveSelection(+1));
        Assert.Equal(0, host.Selected);
    }

    [Fact]
    public void Finishing_a_second_page_world_goes_to_the_interlude_not_the_next_world()
    {
        // Issue #44 step 5. Go(1) flattens across every world, so if a finished
        // world rolled on through it, finishing the fifth would carry straight
        // into scuba. It does not: the fourth cave of any world ends at the
        // interlude, and the interlude leads to the chooser.
        var host = Host(worlds: Menu(0, 2, 4, 6, 8, 1, 3));
        host.Selected = 5;
        host.StartSelectedWorld();
        host.Session.Begin(5, 3);
        host.Load(5, 3);

        host.Sim!.CaveComplete = true;
        host.Tick(Still);

        Assert.Equal(GameScreen.Interlude, host.Screen);
        Assert.Equal(5, host.World);
    }
}
