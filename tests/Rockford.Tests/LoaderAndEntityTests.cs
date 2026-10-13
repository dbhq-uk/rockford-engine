using Rockford.Engine;
using Rockford.Formats;
using Xunit;

namespace Rockford.Tests;

public class LoaderAndEntityTests
{
    private static string? DataDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("ROCKFORD_DATA");
            if (string.IsNullOrWhiteSpace(dir)) return null;
            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "CELLMAPS.BIN"))) return dir;

            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                if (!File.Exists(Path.Combine(d.FullName, "Rockford.slnx"))) continue;
                var c = Path.Combine(d.FullName, dir);
                return File.Exists(Path.Combine(c, "CELLMAPS.BIN")) ? c : null;
            }

            return null;
        }
    }

    private static byte[]? Record(int cave)
    {
        if (DataDir is null) return null;
        var all = File.ReadAllBytes(Path.Combine(DataDir, "CELLMAPS.BIN"));
        return all[(cave * CaveLoader.RecordSize)..((cave + 1) * CaveLoader.RecordSize)];
    }

    [SkippableFact]
    public void Every_shipped_cave_has_one_player_start_and_one_exit()
    {
        Skip.If(DataDir is null, "Set ROCKFORD_DATA to an extracted game directory.");

        for (var cave = 0; cave < 40; cave++)
        {
            var loaded = CaveLoader.Load(Record(cave)!);

            Assert.True(loaded.PlayerCell >= 0, $"cave {cave} has no player start");
            Assert.Equal(RuntimeTile.Player, loaded.Grid.ValueAt(loaded.PlayerCell));
            Assert.Equal(1, loaded.Grid.Count(RuntimeTile.ExitClosed));
        }
    }

    [SkippableFact]
    public void The_player_starts_where_authored_twenty_is_not_where_authored_seven_four_is()
    {
        Skip.If(DataDir is null, "Set ROCKFORD_DATA to an extracted game directory.");

        var record = Record(0)!;
        var loaded = CaveLoader.Load(record);

        // In cave 1 the authored bytes sit at these two places. The old engine started
        // Rockford on the 0x74 cell, which is the exit.
        var authoredAtPlayer = record[CaveGrid.RowOf(loaded.PlayerCell) * 40
                                      + CaveGrid.ColumnOf(loaded.PlayerCell)];
        Assert.Equal(0x20, authoredAtPlayer);

        var exit = -1;
        foreach (var a in loaded.Grid.ScanOrder())
            if (loaded.Grid.ValueAt(a) == RuntimeTile.ExitClosed) exit = a;

        Assert.Equal(0x74, record[CaveGrid.RowOf(exit) * 40 + CaveGrid.ColumnOf(exit)]);
        Assert.NotEqual(loaded.PlayerCell, exit);
    }

    [SkippableFact]
    public void Authored_zero_four_is_the_timed_wall_and_zero_eight_is_the_expanding_wall()
    {
        Skip.If(DataDir is null, "Set ROCKFORD_DATA to an extracted game directory.");

        var grid = new CaveGrid();
        var record = new byte[CaveLoader.RecordSize];
        record[0] = 0x04;
        record[1] = 0x08;
        record[2] = 0x0C;
        record[3] = 0x10;
        var loaded = CaveLoader.Load(record);
        grid = loaded.Grid;

        Assert.Equal(RuntimeTile.TimedWall, grid.ValueAt(0, 0));
        Assert.Equal(RuntimeTile.ExpandingWall, grid.ValueAt(1, 0));
        Assert.Equal(RuntimeTile.PassThroughWall, grid.ValueAt(2, 0));
        Assert.Equal(RuntimeTile.Amoeba, grid.ValueAt(3, 0));
        Assert.True(loaded.HasAmoeba);
    }

    [SkippableFact]
    public void The_three_rotations_of_the_clockwise_family_are_one_family()
    {
        Skip.If(DataDir is null, "Set ROCKFORD_DATA to an extracted game directory.");

        var record = new byte[CaveLoader.RecordSize];
        record[0] = 0x2C;
        record[1] = 0x2E;
        record[2] = 0x30;
        record[3] = 0x34;
        var g = CaveLoader.Load(record).Grid;

        // Authored 0x2C, 0x2E and 0x30 are all the same creature facing three ways.
        Assert.Equal(RuntimeTile.EnemyClockwise, g.ValueAt(0, 0));
        Assert.Equal(RuntimeTile.EnemyClockwise, g.ValueAt(1, 0));
        Assert.Equal(RuntimeTile.EnemyClockwise, g.ValueAt(2, 0));
        Assert.Equal(RuntimeTile.EnemyAnticlockwise, g.ValueAt(3, 0));

        Assert.Equal(Simulation.DirLeft, g.StateAt(0, 0) & 3);
        Assert.Equal(Simulation.DirRight, g.StateAt(1, 0) & 3);
        Assert.Equal(Simulation.DirDown, g.StateAt(2, 0) & 3);
        Assert.Equal(Simulation.DirUp, g.StateAt(3, 0) & 3);
    }

    private static CaveGrid Room()
    {
        var g = new CaveGrid();
        for (var y = 1; y < CaveGrid.Height - 1; y++)
        for (var x = 1; x < CaveGrid.Width - 1; x++)
            g.WriteWord(x, y, 0xFF00 | RuntimeTile.Space);
        return g;
    }

    [Fact]
    public void A_blocked_enemy_turns_but_does_not_move()
    {
        var g = new CaveGrid();   // all steel
        g[CaveGrid.AddressOf(5, 5)] = RuntimeTile.EnemyClockwise;
        g[CaveGrid.AddressOf(5, 5) + 1] = Simulation.FacingTag | Simulation.DirUp;

        new Simulation(g, 600).Step();

        Assert.Equal(RuntimeTile.EnemyClockwise, g.ValueAt(5, 5));

        // Two attempts fail, then it turns once more the same way as the second turn.
        // Starting from up, the clockwise family ends facing left.
        Assert.Equal(Simulation.DirLeft, g.StateAt(5, 5) & 3);
    }

    [Fact]
    public void The_two_families_turn_opposite_ways()
    {
        var cw = new CaveGrid();
        cw[CaveGrid.AddressOf(5, 5)] = RuntimeTile.EnemyClockwise;
        cw[CaveGrid.AddressOf(5, 5) + 1] = Simulation.FacingTag | Simulation.DirUp;
        new Simulation(cw, 600).Step();
        Assert.Equal(Simulation.DirLeft, cw.StateAt(5, 5) & 3);

        var acw = new CaveGrid();
        acw[CaveGrid.AddressOf(5, 5)] = RuntimeTile.EnemyAnticlockwise;
        acw[CaveGrid.AddressOf(5, 5) + 1] = Simulation.FacingTag | Simulation.DirUp;
        new Simulation(acw, 600).Step();
        Assert.Equal(Simulation.DirRight, acw.StateAt(5, 5) & 3);
    }

    [Fact]
    public void An_enemy_moving_right_lands_as_the_just_moved_state_and_is_restored()
    {
        var g = Room();
        g[CaveGrid.AddressOf(5, 5)] = RuntimeTile.EnemyClockwise;
        g[CaveGrid.AddressOf(5, 5) + 1] = Simulation.FacingTag | Simulation.DirUp;

        new Simulation(g, 600).Step();

        // Facing up, the clockwise family turns to right and moves there. The cell is
        // ahead of the scan, so it lands as 0x8D and the same pass restores it.
        Assert.Equal(RuntimeTile.Space, g.ValueAt(5, 5));
        Assert.Equal(RuntimeTile.EnemyClockwise, g.ValueAt(6, 5));
    }

    [Fact]
    public void An_enemy_cannot_dig()
    {
        var g = new CaveGrid();
        for (var y = 1; y < CaveGrid.Height - 1; y++)
        for (var x = 1; x < CaveGrid.Width - 1; x++)
            g.WriteWord(x, y, 0xFF00 | RuntimeTile.Dirt);
        g[CaveGrid.AddressOf(5, 5)] = RuntimeTile.EnemyClockwise;
        g[CaveGrid.AddressOf(5, 5) + 1] = Simulation.FacingTag | Simulation.DirUp;

        new Simulation(g, 600).Step();

        Assert.Equal(RuntimeTile.EnemyClockwise, g.ValueAt(5, 5));
    }

    [Fact]
    public void An_expanding_wall_waits_a_tick_then_grows_one_cell_each_way_per_tick()
    {
        var g = Room();
        g.WriteWord(10, 5, 0xFF15);     // as the loader writes it, both sides latched

        var sim = new Simulation(g, 600);

        sim.Step();
        Assert.Equal(RuntimeTile.Space, g.ValueAt(9, 5));
        Assert.Equal(RuntimeTile.Space, g.ValueAt(11, 5));

        sim.Step();
        Assert.Equal(RuntimeTile.ExpandingWall, g.ValueAt(9, 5));
        Assert.Equal(RuntimeTile.ExpandingWall, g.ValueAt(11, 5));

        // One cell per tick, never a whole row in one pass.
        Assert.Equal(RuntimeTile.Space, g.ValueAt(8, 5));
        Assert.Equal(RuntimeTile.Space, g.ValueAt(12, 5));
    }

    [Fact]
    public void The_exit_opens_only_when_the_quota_is_met()
    {
        var g = Room();
        g.WriteWord(10, 5, 0xFF00 | RuntimeTile.ExitClosed);

        var sim = new Simulation(g, 600) { TreasureQuota = 1 };
        sim.Step();
        Assert.Equal(RuntimeTile.ExitClosed, g.ValueAt(10, 5));

        sim.TreasureQuota = 0;
        sim.Step();
        Assert.True(RuntimeTile.IsExitOpen(g.ValueAt(10, 5)));
    }

    [Fact]
    public void An_open_exit_animates_on_alternate_ticks_and_wraps()
    {
        // The sequence, not the set. This test used to collect the values into a
        // HashSet, and an exit that animated on EVERY scan produced exactly the
        // same four values - so the alternation its own name describes was not
        // being checked at all. Only an ordered comparison can see a cadence.
        //
        // 0xEDD5 increments the frame counter AFTER the scan, so the first step
        // sees zero and does not advance. Each value therefore lands twice, and
        // the fourth wraps rather than running on into the closed exit.
        var g = Room();
        g.WriteWord(10, 5, 0xFF30);

        var sim = new Simulation(g, 600) { TreasureQuota = 0 };
        var seen = new List<byte>();
        for (var i = 0; i < 12; i++)
        {
            sim.Step();
            seen.Add(g.ValueAt(10, 5));
        }

        Assert.Equal(
            [0x30, 0x31, 0x31, 0x32, 0x32, 0x33, 0x33, 0x30, 0x30, 0x31, 0x31, 0x32],
            seen);
    }
}
