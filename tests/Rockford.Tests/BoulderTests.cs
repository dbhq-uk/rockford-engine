using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The boulder handlers, checked against the original at <c>0x13EF7</c> and
/// <c>0x13FD0</c>. Several of these encode behaviour the previous engine had wrong,
/// so they are written to fail if it comes back.
/// </summary>
public class BoulderTests
{
    /// <summary>A cave of dirt with a steel border, which is what every shipped cave is.</summary>
    private static CaveGrid Cave()
    {
        var g = new CaveGrid();
        for (var y = 1; y < CaveGrid.Height - 1; y++)
        for (var x = 1; x < CaveGrid.Width - 1; x++)
            g.WriteWord(x, y, 0xFF00 | RuntimeTile.Dirt);
        return g;
    }

    private static void Put(CaveGrid g, int x, int y, byte v) => g.WriteWord(x, y, (ushort)(0xFF00 | v));

    [Fact]
    public void A_boulder_over_space_starts_falling_without_moving()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.Boulder);
        Put(g, 5, 6, RuntimeTile.Space);

        new Simulation(g, 600).Step();

        // It is still where it was, but now in the falling state. The original takes a
        // whole step to commit to a fall before it displaces anything.
        Assert.Equal(RuntimeTile.BoulderFalling, g.ValueAt(5, 5));
        Assert.Equal(RuntimeTile.Space, g.ValueAt(5, 6));
    }

    [Fact]
    public void The_second_step_is_the_one_that_moves_it()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.Boulder);
        Put(g, 5, 6, RuntimeTile.Space);
        Put(g, 5, 7, RuntimeTile.Space);

        var sim = new Simulation(g, 600);
        sim.Step();
        sim.Step();

        Assert.Equal(RuntimeTile.Space, g.ValueAt(5, 5));

        // It moved down exactly one row. The move wrote a deferred faller, and the
        // same scan then reached that cell and promoted it to a live one - which is
        // all the deferred state is for. It spends a visit instead of falling twice.
        Assert.Equal(RuntimeTile.BoulderFalling, g.ValueAt(5, 6));
        Assert.Equal(RuntimeTile.Space, g.ValueAt(5, 7));
    }

    [Fact]
    public void Rolling_right_costs_a_visit_so_it_cannot_also_fall_this_step()
    {
        // Rolling LEFT lands on a cell the scan has already passed, so a live faller
        // is safe there. Rolling RIGHT lands on a cell the scan still has to reach, so
        // the original writes a deferred faller which that visit merely promotes. Both
        // end the step as a live faller one cell sideways, having fallen no further.
        var left = Cave();
        Put(left, 5, 5, RuntimeTile.Boulder);
        Put(left, 5, 6, RuntimeTile.Boulder);      // a rounded shoulder
        Put(left, 4, 5, RuntimeTile.Space);
        Put(left, 4, 6, RuntimeTile.Space);
        new Simulation(left, 600).Step();
        Assert.Equal(RuntimeTile.BoulderFalling, left.ValueAt(4, 5));
        Assert.Equal(RuntimeTile.Space, left.ValueAt(4, 6));

        var right = Cave();
        Put(right, 5, 5, RuntimeTile.Boulder);
        Put(right, 5, 6, RuntimeTile.Boulder);
        Put(right, 6, 5, RuntimeTile.Space);
        Put(right, 6, 6, RuntimeTile.Space);
        new Simulation(right, 600).Step();
        Assert.Equal(RuntimeTile.BoulderFalling, right.ValueAt(6, 5));
        Assert.Equal(RuntimeTile.Space, right.ValueAt(6, 6));
    }

    [Fact]
    public void Left_is_tried_before_right()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.Boulder);
        Put(g, 5, 6, RuntimeTile.Boulder);
        Put(g, 4, 5, RuntimeTile.Space);
        Put(g, 4, 6, RuntimeTile.Space);
        Put(g, 6, 5, RuntimeTile.Space);
        Put(g, 6, 6, RuntimeTile.Space);

        new Simulation(g, 600).Step();

        Assert.Equal(RuntimeTile.BoulderFalling, g.ValueAt(4, 5));
        Assert.Equal(RuntimeTile.Space, g.ValueAt(6, 5));
    }

    [Fact]
    public void A_boulder_rests_on_dirt_and_on_steel()
    {
        foreach (var ground in new[] { RuntimeTile.Dirt, RuntimeTile.Steel })
        {
            var g = Cave();
            Put(g, 5, 5, RuntimeTile.Boulder);
            Put(g, 5, 6, ground);
            Put(g, 4, 5, RuntimeTile.Space);
            Put(g, 4, 6, RuntimeTile.Space);

            new Simulation(g, 600).Step();

            // Dirt and steel are not in the rounded-shoulder list, so there is no roll.
            Assert.Equal(RuntimeTile.Boulder, g.ValueAt(5, 5));
            Assert.Equal(RuntimeTile.Space, g.ValueAt(4, 5));
        }
    }

    [Fact]
    public void A_falling_boulder_rolls_over_an_enemy_but_a_resting_one_does_not()
    {
        var falling = Cave();
        Put(falling, 5, 5, RuntimeTile.BoulderFalling);
        Put(falling, 5, 6, RuntimeTile.Boulder);
        Put(falling, 4, 5, RuntimeTile.Space);
        Put(falling, 4, 6, RuntimeTile.EnemyClockwise);
        new Simulation(falling, 600).Step();
        Assert.Equal(RuntimeTile.BoulderFalling, falling.ValueAt(4, 5));

        var resting = Cave();
        Put(resting, 5, 5, RuntimeTile.Boulder);
        Put(resting, 5, 6, RuntimeTile.Boulder);
        Put(resting, 4, 5, RuntimeTile.Space);
        Put(resting, 4, 6, RuntimeTile.EnemyClockwise);
        new Simulation(resting, 600).Step();
        Assert.Equal(RuntimeTile.Boulder, resting.ValueAt(5, 5));
    }

    [Fact]
    public void The_two_enemy_families_leave_different_things_behind()
    {
        // The clockwise family explodes into treasure, the other into space. The old
        // engine gave both of them treasure.
        var clockwise = Cave();
        Put(clockwise, 5, 5, RuntimeTile.BoulderFalling);
        Put(clockwise, 5, 6, RuntimeTile.EnemyClockwise);
        new Simulation(clockwise, 600).Step();
        // The blasted cell is below the trigger, so the same scan reaches it and
        // advances the phase once. What matters is which chain it is in.
        Assert.InRange(clockwise.ValueAt(5, 6), RuntimeTile.TreasureBlast0, RuntimeTile.TreasureBlast3);

        var anticlockwise = Cave();
        Put(anticlockwise, 5, 5, RuntimeTile.BoulderFalling);
        Put(anticlockwise, 5, 6, RuntimeTile.EnemyAnticlockwise);
        new Simulation(anticlockwise, 600).Step();
        Assert.InRange(anticlockwise.ValueAt(5, 6), RuntimeTile.SpaceBlast0, RuntimeTile.SpaceBlast3);
    }

    [Fact]
    public void A_falling_boulder_kills_the_player_and_a_resting_one_does_not()
    {
        var killed = Cave();
        Put(killed, 5, 5, RuntimeTile.BoulderFalling);
        Put(killed, 5, 6, RuntimeTile.Player);
        var sim = new Simulation(killed, 600) { PlayerCell = CaveGrid.AddressOf(5, 6) };
        sim.Step();
        Assert.True(sim.PlayerDead);

        var safe = Cave();
        Put(safe, 5, 5, RuntimeTile.Boulder);
        Put(safe, 5, 6, RuntimeTile.Player);
        var sim2 = new Simulation(safe, 600) { PlayerCell = CaveGrid.AddressOf(5, 6) };
        sim2.Step();
        Assert.False(sim2.PlayerDead);
    }

    [Fact]
    public void The_exit_survives_a_space_blast_but_not_a_treasure_blast()
    {
        // 0x11360 spares the exit, 0x12AAE does not. So which enemy dies next to an
        // exit decides whether the exit is still there afterwards.
        var spared = Cave();
        Put(spared, 5, 5, RuntimeTile.BoulderFalling);
        Put(spared, 5, 6, RuntimeTile.EnemyAnticlockwise);   // kind 2
        Put(spared, 4, 6, RuntimeTile.ExitClosed);
        // A quota still outstanding, or the scan would open the exit on this same pass
        // and the test would be measuring the wrong thing.
        new Simulation(spared, 600) { TreasureQuota = 1 }.Step();
        Assert.Equal(RuntimeTile.ExitClosed, spared.ValueAt(4, 6));

        var destroyed = Cave();
        Put(destroyed, 5, 5, RuntimeTile.BoulderFalling);
        Put(destroyed, 5, 6, RuntimeTile.EnemyClockwise);    // kind 1
        Put(destroyed, 4, 6, RuntimeTile.ExitClosed);
        new Simulation(destroyed, 600) { TreasureQuota = 1 }.Step();
        Assert.NotEqual(RuntimeTile.ExitClosed, destroyed.ValueAt(4, 6));
    }

    [Fact]
    public void Steel_survives_either_blast()
    {
        foreach (var enemy in new[] { RuntimeTile.EnemyClockwise, RuntimeTile.EnemyAnticlockwise })
        {
            var g = Cave();
            Put(g, 5, 5, RuntimeTile.BoulderFalling);
            Put(g, 5, 6, enemy);
            Put(g, 4, 6, RuntimeTile.Steel);
            new Simulation(g, 600).Step();
            Assert.Equal(RuntimeTile.Steel, g.ValueAt(4, 6));
        }
    }

    [Fact]
    public void An_explosion_counts_up_to_treasure_or_to_space()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.TreasureBlast0);
        Put(g, 8, 5, RuntimeTile.SpaceBlast0);

        var sim = new Simulation(g, 600);
        for (var i = 0; i < 4; i++) sim.Step();

        Assert.Equal(RuntimeTile.Treasure, g.ValueAt(5, 5));
        Assert.Equal(RuntimeTile.Space, g.ValueAt(8, 5));
    }
}
