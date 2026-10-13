using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

public class AmoebaAndTreasureTests
{
    private static CaveGrid Cave()
    {
        var g = new CaveGrid();
        for (var y = 1; y < CaveGrid.Height - 1; y++)
        for (var x = 1; x < CaveGrid.Width - 1; x++)
            g.WriteWord(x, y, 0xFF00 | RuntimeTile.Dirt);
        return g;
    }

    private static void Put(CaveGrid g, int x, int y, byte v) => g.WriteWord(x, y, (ushort)(0xFF00 | v));

    private static void Fill(CaveGrid g, int x0, int y0, int x1, int y1, byte v)
    {
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
            Put(g, x, y, v);
    }

    [Fact]
    public void The_timed_wall_swaps_what_falls_through_it()
    {
        // A boulder comes out as treasure and treasure comes out as a boulder. The
        // pass-through wall, by contrast, keeps the type.
        var boulder = Cave();
        Put(boulder, 5, 5, RuntimeTile.Boulder);
        Put(boulder, 5, 6, RuntimeTile.TimedWall);
        Put(boulder, 5, 7, RuntimeTile.Space);
        new Simulation(boulder, 600).Step();
        // Written as deferred, then promoted when the same scan reaches that row.
        Assert.Equal(RuntimeTile.TreasureFalling, boulder.ValueAt(5, 7));

        var treasure = Cave();
        Put(treasure, 5, 5, RuntimeTile.Treasure);
        Put(treasure, 5, 6, RuntimeTile.TimedWall);
        Put(treasure, 5, 7, RuntimeTile.Space);
        new Simulation(treasure, 600).Step();
        Assert.Equal(RuntimeTile.BoulderFalling, treasure.ValueAt(5, 7));
    }

    [Fact]
    public void A_blocked_timed_wall_destroys_what_enters_it()
    {
        // The input cell is cleared before the output is checked for room.
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.Boulder);
        Put(g, 5, 6, RuntimeTile.TimedWall);
        Put(g, 5, 7, RuntimeTile.Steel);

        new Simulation(g, 600).Step();

        Assert.NotEqual(RuntimeTile.Boulder, g.ValueAt(5, 5));
        Assert.Equal(RuntimeTile.Steel, g.ValueAt(5, 7));
    }

    [Fact]
    public void A_falling_object_lands_on_the_timed_wall_and_converts_a_step_later()
    {
        // The falling handler has no wall case at all, so it rests on top and the
        // resting handler does the work next scan. Doing it in one step fires the wall
        // a frame early.
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.BoulderFalling);
        Put(g, 5, 6, RuntimeTile.TimedWall);
        Put(g, 5, 7, RuntimeTile.Space);

        var sim = new Simulation(g, 600);
        sim.Step();
        Assert.Equal(RuntimeTile.Boulder, g.ValueAt(5, 5));
        Assert.Equal(RuntimeTile.Space, g.ValueAt(5, 7));

        sim.Step();
        Assert.Equal(RuntimeTile.TreasureFalling, g.ValueAt(5, 7));
    }

    [Fact]
    public void A_falling_gem_on_the_player_leaves_treasure_where_a_boulder_leaves_space()
    {
        var gem = Cave();
        Put(gem, 5, 5, RuntimeTile.TreasureFalling);
        Put(gem, 5, 6, RuntimeTile.Player);
        Put(gem, 7, 6, RuntimeTile.Dirt);
        var s1 = new Simulation(gem, 600) { PlayerCell = CaveGrid.AddressOf(5, 6) };
        s1.Step();
        Assert.True(s1.PlayerDead);
        Assert.InRange(gem.ValueAt(4, 6), RuntimeTile.TreasureBlast0, RuntimeTile.TreasureBlast3);

        var rock = Cave();
        Put(rock, 5, 5, RuntimeTile.BoulderFalling);
        Put(rock, 5, 6, RuntimeTile.Player);
        var s2 = new Simulation(rock, 600) { PlayerCell = CaveGrid.AddressOf(5, 6) };
        s2.Step();
        Assert.True(s2.PlayerDead);
        Assert.InRange(rock.ValueAt(4, 6), RuntimeTile.SpaceBlast0, RuntimeTile.SpaceBlast3);
    }

    [Fact]
    public void Death_is_a_countdown_not_an_instant_end()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.BoulderFalling);
        Put(g, 5, 6, RuntimeTile.Player);
        var sim = new Simulation(g, 60) { PlayerCell = CaveGrid.AddressOf(5, 6) };
        sim.Step();

        // Seven, not eight. The blast writes 8 during the scan at 0xEDC2, and the
        // countdown at 0xEE76 runs later in the SAME iteration and takes it to 7.
        // This asserted 8 while the countdown ran before the scan, which gave every
        // death one frame more than the original.
        Assert.Equal(7, sim.PlayerDeathTimer);
    }

    [Fact]
    public void An_enclosed_amoeba_becomes_treasure()
    {
        // Walled in with steel on all four sides, so nothing ever clears the flag.
        var g = Cave();
        Fill(g, 4, 4, 6, 6, RuntimeTile.Steel);
        Put(g, 5, 5, RuntimeTile.Amoeba);

        var sim = new Simulation(g, 600) { CaveHasAmoeba = true };
        sim.Step();

        // It enters the treasure chain at the end of the scan and resolves from there.
        Assert.InRange(g.ValueAt(5, 5), RuntimeTile.TreasureBlast0, RuntimeTile.TreasureBlast3);
        for (var i = 0; i < 4; i++) sim.Step();
        Assert.Equal(RuntimeTile.Treasure, g.ValueAt(5, 5));
    }

    [Fact]
    public void An_amoeba_with_room_is_not_enclosed_and_survives()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.Amoeba);
        Put(g, 5, 4, RuntimeTile.Space);

        var sim = new Simulation(g, 600) { CaveHasAmoeba = true };
        sim.Step();

        Assert.True(RuntimeTile.IsAmoeba(g.ValueAt(5, 5)));
    }

    [Fact]
    public void An_oversized_amoeba_becomes_falling_boulders()
    {
        // The opposite outcome to being enclosed, and the old engine had the pair the
        // wrong way round.
        var g = Cave();
        Fill(g, 2, 2, 11, 11, RuntimeTile.Amoeba);   // 100 cells

        var sim = new Simulation(g, 600) { CaveHasAmoeba = true, AmoebaCap = 20 };
        sim.Step();

        Assert.False(sim.CaveHasAmoeba);
        Assert.Equal(0, g.Count(RuntimeTile.Amoeba));
        Assert.True(g.Count(RuntimeTile.BoulderFalling) + g.Count(RuntimeTile.Boulder) > 0);
    }

    [Fact]
    public void An_amoeba_beside_a_creature_detonates_it_and_is_caught_in_the_blast()
    {
        var g = Cave();
        Fill(g, 4, 4, 6, 6, RuntimeTile.Steel);
        Put(g, 5, 5, RuntimeTile.Amoeba);
        Put(g, 5, 4, RuntimeTile.EnemyClockwise);

        var sim = new Simulation(g, 600) { CaveHasAmoeba = true };
        sim.Step();

        // The creature dies, and the 3 by 3 is centred on it, so the amoeba cell that
        // triggered the blast is inside it and goes too. Touching a creature is not a
        // free kill for the amoeba.
        Assert.NotEqual(RuntimeTile.EnemyClockwise, g.ValueAt(5, 4));
        Assert.False(RuntimeTile.IsAmoeba(g.ValueAt(5, 5)));
    }

    [Fact]
    public void Only_an_open_neighbour_costs_a_random_draw()
    {
        // A boxed-in amoeba must not touch the random stream at all, or every later
        // random event in the game drifts.
        var boxed = Cave();
        Fill(boxed, 4, 4, 6, 6, RuntimeTile.Steel);
        Put(boxed, 5, 5, RuntimeTile.Amoeba);
        var s1 = new Simulation(boxed, 600) { CaveHasAmoeba = true };
        s1.Step();
        Assert.Equal(0, s1.Random.Seed);

        // One open side is one draw.
        var open = Cave();
        Fill(open, 4, 4, 6, 6, RuntimeTile.Steel);
        Put(open, 5, 5, RuntimeTile.Amoeba);
        Put(open, 5, 4, RuntimeTile.Space);
        var s2 = new Simulation(open, 600) { CaveHasAmoeba = true };
        s2.Step();
        Assert.Equal(new GameRandom(0).Next(), s2.Random.Seed);
    }

    [Fact]
    public void The_amoeba_animates_through_its_four_frames()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.Amoeba);
        Put(g, 5, 4, RuntimeTile.Space);
        var sim = new Simulation(g, 600) { CaveHasAmoeba = true };

        for (byte expected = 0x49; expected <= 0x4B; expected++)
        {
            sim.Step();
            Assert.Equal(expected, g.ValueAt(5, 5));
        }

        sim.Step();
        Assert.Equal(RuntimeTile.Amoeba, g.ValueAt(5, 5));   // wraps back to 0x48
    }
}
