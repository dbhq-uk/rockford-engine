using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// A whole-state fingerprint of a scripted run, and the guarantee that adding to
/// the engine has not changed what it does.
///
/// <para>
/// The numbers below were captured from the engine at <c>45808ad</c>, BEFORE the
/// read-only observation surface was added. An observation that only appends to a
/// list cannot change a simulation, and this is what turns that from a claim into
/// a test.
/// </para>
///
/// <para>
/// <b>The first version of this was much weaker than it was described as being,
/// and the correction is the reason for most of what is below.</b> It built a
/// <see cref="Simulation"/> and never set <see cref="Simulation.PlayerCell"/>.
/// The default is <c>-1</c> and <c>PlayerTick</c> returns immediately on that, so
/// putting a player tile in the grid did nothing - all three traces ran with an
/// inert player. No walking, no firing, no pushing, no collecting, and no per-tick
/// draw from the generator at <c>0x11DAC</c>, which moves the whole random stream.
/// The instrumentation added to <c>Walk</c> was covered by none of it. Found by
/// Astra, 19 September 2026: docs/astra-review-2026-09-19.md.
/// </para>
///
/// <para>
/// So: the player is live and <see cref="Live"/> asserts it, every scenario
/// asserts that the thing it is named for actually happened, the state absorbed
/// covers what was left out, and a <see cref="GameHost"/> trace carries lives,
/// cave transitions and the globals that outlive a cave.
/// </para>
///
/// <para>
/// If one of these fails, the engine's behaviour has changed. That is either a bug
/// or a deliberate correction, and either way it needs saying out loud rather than
/// being absorbed by updating the number. The port is checked against a
/// disassembly; a silent change here is a silent divergence from it.
/// </para>
///
/// <para>
/// The caves are built in code, so this runs on a clean checkout with no game data
/// - which is exactly where a fidelity guarantee has to run.
/// </para>
/// </summary>
public class FingerprintTests
{
    /// <summary>Dirt inside a steel border, which is the shape of every shipped cave.</summary>
    private static CaveGrid DirtCave()
    {
        var g = new CaveGrid();
        for (var y = 1; y < CaveGrid.Height - 1; y++)
        for (var x = 1; x < CaveGrid.Width - 1; x++)
            g.WriteWord(x, y, 0xFF00 | RuntimeTile.Dirt);
        return g;
    }

    private static void Put(CaveGrid g, int x, int y, byte v) =>
        g.WriteWord(x, y, (ushort)(0xFF00 | v));

    /// <summary>Where the player starts in every cave below.</summary>
    private const int PlayerX = 5;
    private const int PlayerY = 10;

    /// <summary>
    /// A simulation with a player that can actually act.
    ///
    /// The grid tile and the pointer are two separate things and the pointer is
    /// the one the player routine reads. Setting only the first is what made the
    /// first version of this file measure nothing.
    /// </summary>
    private static Simulation Live(CaveGrid grid, ushort seed, int seconds = 600)
    {
        Put(grid, PlayerX, PlayerY, RuntimeTile.Player);
        return new Simulation(grid, seconds, randomSeed: seed)
        {
            PlayerCell = CaveGrid.AddressOf(PlayerX, PlayerY),
        };
    }

    /// <summary>
    /// A cave with something of everything in it, laid out so the interesting cases
    /// actually happen: boulders that fall and roll off one another, treasure that
    /// falls and can be collected, both enemy families, an amoeba, and hollow space
    /// for all of it to move through.
    ///
    /// Hand-placed rather than generated, because the point is that it is the SAME
    /// cave every time this test is ever run.
    /// </summary>
    private static CaveGrid BusyCave()
    {
        var g = DirtCave();

        for (var y = 3; y <= 16; y++)
        for (var x = 3; x <= 26; x++)
            Put(g, x, y, RuntimeTile.Space);

        // A stack that has to roll: three boulders shoulder to shoulder over a gap.
        Put(g, 8, 4, RuntimeTile.Boulder);
        Put(g, 9, 4, RuntimeTile.Boulder);
        Put(g, 10, 4, RuntimeTile.Boulder);
        Put(g, 9, 5, RuntimeTile.Boulder);

        Put(g, 14, 4, RuntimeTile.Treasure);
        Put(g, 15, 4, RuntimeTile.Treasure);
        Put(g, 15, 9, RuntimeTile.Boulder);

        // Treasure and a boulder within reach of the player's own row, so walking
        // and firing along it collect and push rather than swiping at nothing.
        Put(g, 7, PlayerY, RuntimeTile.Treasure);
        Put(g, 9, PlayerY, RuntimeTile.Boulder);
        Put(g, 11, PlayerY, RuntimeTile.Treasure);

        // A shelf for things to come to rest on, with a hole in it.
        for (var x = 3; x <= 26; x++)
            if (x != 12) Put(g, x, 12, RuntimeTile.Wall);

        Put(g, 20, 6, RuntimeTile.EnemyClockwise);
        Put(g, 22, 8, RuntimeTile.EnemyAnticlockwise);

        // The amoeba draws from the generator on every growth attempt, so it is the
        // most sensitive thing here to the random stream moving at all.
        Put(g, 6, 14, RuntimeTile.Amoeba);

        Put(g, 25, 15, RuntimeTile.ExitClosed);

        return g;
    }

    /// <summary>
    /// A fixed input trace. Walking, firing, standing still and pressing into a
    /// wall, in a pattern long enough that a divergence anywhere has room to show.
    /// </summary>
    private static PlayerAction ActionFor(int step) => (step % 12) switch
    {
        0 or 1 => PlayerAction.Walk(PlayerAction.Right),
        2 => PlayerAction.Shoot(PlayerAction.Right),
        3 => PlayerAction.Idle,
        4 or 5 => PlayerAction.Walk(vertical: PlayerAction.Down),
        6 => PlayerAction.Shoot(vertical: PlayerAction.Down),
        7 or 8 => PlayerAction.Walk(PlayerAction.Left),
        9 => PlayerAction.Walk(vertical: PlayerAction.Up),
        10 => PlayerAction.Shoot(PlayerAction.Left),
        _ => PlayerAction.Idle,
    };


    /// <summary>What a trace did, so a scenario can assert it actually did it.</summary>
    private readonly record struct Ran(string Hash, bool PlayerMoved, bool PlayerFired, bool SomethingFell);

    /// <summary>Runs the trace and returns the rolled-up fingerprint and what happened.</summary>
    private static Ran Run(CaveGrid grid, int steps, ushort seed, int seconds = 600)
    {
        var sim = Live(grid, seed, seconds);
        var print = new Fingerprint();

        var startedAt = sim.PlayerCell;
        var moved = false;
        var fired = false;
        var fell = false;

        for (var step = 0; step < steps; step++)
        {
            var action = ActionFor(step);
            var was = sim.PlayerCell;

            sim.PlayerTick(action);
            sim.Step();

            if (sim.PlayerCell != was) moved = true;
            if (action.Fire && action.AnyDirection) fired = true;
            if (grid.Count(RuntimeTile.BoulderFalling) > 0
                || grid.Count(RuntimeTile.TreasureFalling) > 0) fell = true;

            print.Absorb(sim);
        }

        // Not an assertion here, because the quiet cave legitimately drops nothing.
        // Handed back so each scenario can say what it expects of itself.
        _ = startedAt;
        return new Ran(print.ToString(), moved, fired, fell);
    }

    [Fact]
    public void The_busy_cave_runs_the_same_as_it_did_before_the_observation_surface()
    {
        var ran = Run(BusyCave(), steps: 200, seed: 0);

        // The scenario has to happen, or the hash is guarding an empty room. This
        // is the check whose absence made the first version of this file useless.
        Assert.True(ran.PlayerMoved, "the player never moved, so the walk is untested");
        Assert.True(ran.PlayerFired, "the player never fired");
        Assert.True(ran.SomethingFell, "nothing ever fell");

        Assert.Equal("fe8aa20934471acd", ran.Hash);
    }

    [Fact]
    public void And_the_same_from_a_different_seed()
    {
        // A second seed, because a change that happens to leave seed zero alone is
        // still a change. The amoeba and the pushes both read the generator.
        var ran = Run(BusyCave(), steps: 200, seed: 0x29A1);

        Assert.True(ran.PlayerMoved, "the player never moved");
        Assert.True(ran.SomethingFell, "nothing ever fell");

        Assert.Equal("6500272e16bcfb3a", ran.Hash);
    }

    [Fact]
    public void A_plain_dirt_cave_with_a_player_runs_the_same_too()
    {
        // Almost nothing happens here, which is the point: it isolates the player,
        // the clock and the footstep from everything the busy cave is doing.
        var ran = Run(DirtCave(), steps: 200, seed: 7);

        Assert.True(ran.PlayerMoved, "the player never moved");
        Assert.True(ran.PlayerFired, "the player never fired");

        Assert.Equal("a99d10363f95ba4b", ran.Hash);
    }

    [Fact]
    public void A_whole_session_runs_the_same_through_the_host()
    {
        // The simulation traces above start and end inside one cave. This one goes
        // through GameHost, so it covers the lives, the score, the holds, the cave
        // transitions and the globals CarryGlobalsFrom hands across - none of which
        // the other three touch at all.
        var host = SessionHost();
        var print = new Fingerprint();

        for (var step = 0; step < 400; step++)
        {
            host.Tick(ActionFor(step));
            print.Absorb(host);
            if (host.Sim is not null) print.Absorb(host.Sim);
        }

        // RE-CAPTURED 20 September 2026, and the reason matters. It was
        // f8bbdb553931fee1. Nothing in the engine moved - the four simulation
        // baselines above are untouched - and this one changed because
        // Absorb(GameHost) now feeds World, Stage and Selected, which it never
        // did. A whole-state hash that cannot tell which CAVE you are on hid a
        // replay defect that only moved the stage; that is why they were added
        // and why this number is different.
        Assert.Equal("a73eb20cc8163a41", print.ToString());
    }

    [Fact]
    public void The_run_is_reproducible_within_a_single_build()
    {
        // Guards the fingerprint itself rather than the engine. If this fails, the
        // four above are measuring something that varies and mean nothing.
        Assert.Equal(Run(BusyCave(), steps: 60, seed: 3).Hash,
                     Run(BusyCave(), steps: 60, seed: 3).Hash);
    }

    /// <summary>
    /// A host over synthetic caves, built the way <c>GameHostTests</c> builds them:
    /// steel walls, a player, treasure within reach and an exit.
    /// </summary>
    private static GameHost SessionHost()
    {
        const int worlds = 2;
        var records = worlds * GameHost.CavesPerWorld;
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

            var row = at + 20 * CaveLoader.AuthoredWidth;
            cellmaps[row + 2] = 0x20;       // the player
            cellmaps[row + 3] = 0x88;       // treasure
            cellmaps[row + 4] = 0x88;
            cellmaps[row + 6] = 0x90;       // a boulder to push
            cellmaps[row + 34] = 0x74;      // the closed exit
        }

        var tables = new CaveTables
        {
            Required = Fill(2),
            Time = Fill(60),
            ScoreBefore = Fill(10),
            ScoreAfter = Fill(20),
            AmoebaCap = Fill(200),
        };

        WorldEntry[] worldList =
        [
            new() { Sheet = "one", World = 0 },
            new() { Sheet = "two", World = 1 },
        ];

        var host = new GameHost(cellmaps, tables, worldList);
        host.Selected = 0;
        host.StartSelectedWorld();
        return host;
    }

    private static int[] Fill(int value) => [.. Enumerable.Repeat(value, 64)];
}
