using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The draw every player tick makes, from <c>0x11DAC</c>, and the pose it picks.
///
/// The port used to skip it. That is invisible on its own - the pose it chooses
/// only decides which of two idle sprites is drawn - but the draw comes off the
/// shared generator, so missing it left the stream one step behind on every tick
/// and moved every later push, amoeba and projectile decision in the cave.
/// </summary>
public class PlayerPoseTests
{
    /// <summary>
    /// A player with room to move. A bare CaveGrid is steel everywhere, including
    /// the cell next door - so a walk test on one can never actually walk, and
    /// every pose assertion passes for the wrong reason.
    /// </summary>
    private static Simulation Sim(ushort seed = 0)
    {
        var grid = new CaveGrid();
        for (var y = 3; y <= 7; y++)
            for (var x = 3; x <= 7; x++)
                grid[CaveGrid.AddressOf(x, y)] = RuntimeTile.Space;

        var sim = new Simulation(grid, 600, randomSeed: seed);
        sim.PlayerCell = CaveGrid.AddressOf(5, 5);
        sim.Grid[sim.PlayerCell] = RuntimeTile.Player;
        return sim;
    }

    [Fact]
    public void An_idle_tick_advances_the_generator()
    {
        // The claim this test exists for: from seed zero one idle tick must leave
        // the generator at 0x1249, where the port previously left it at 0x0000
        // because it never drew at all.
        var sim = Sim(0);

        sim.PlayerTick(PlayerAction.Idle);

        Assert.Equal(0x1249, sim.Random.Seed);
    }

    [Fact]
    public void Firing_draws_too()
    {
        // 0x11DAC runs before the directions are even read, so every tick spends a
        // draw whatever the player does with it.
        var idle = Sim();
        var firing = Sim();

        idle.PlayerTick(PlayerAction.Idle);
        firing.PlayerTick(new PlayerAction(PlayerAction.Left, 0, Fire: true));

        Assert.Equal(idle.Random.Seed, firing.Random.Seed);
    }

    [Fact]
    public void Ten_ticks_spend_ten_draws()
    {
        var ticked = Sim();
        var drawn = Sim();

        for (var i = 0; i < 10; i++) ticked.PlayerTick(PlayerAction.Idle);
        for (var i = 0; i < 10; i++) drawn.Random.NextByte();

        Assert.Equal(drawn.Random.Seed, ticked.Random.Seed);
    }

    [Fact]
    public void A_dying_player_makes_no_draw()
    {
        // 0xEDB2 skips the whole player routine while the death timer is set, so
        // the eight frames of an explosion do not quietly advance the stream.
        var sim = Sim();
        sim.Blast(sim.PlayerCell - Simulation.BlastCentreOffset, kind: 1);
        var before = sim.Random.Seed;

        sim.PlayerTick(PlayerAction.Idle);

        Assert.Equal(before, sim.Random.Seed);
    }

    [Fact]
    public void The_idle_pose_is_one_of_the_two_and_uses_both()
    {
        // 0x11DAF tests the low two bits: non-zero picks 0x18, zero picks 0x20. A
        // port that always chose one would pass a weaker test than this.
        var seen = new HashSet<int>();

        for (ushort seed = 0; seed < 200; seed++)
        {
            var sim = Sim(seed);
            sim.PlayerTick(PlayerAction.Idle);
            seen.Add(sim.PlayerPose);
        }

        Assert.Equal(new HashSet<int> { Simulation.PoseIdleA, Simulation.PoseIdleB }, seen);
    }

    [Theory]
    [InlineData(PlayerAction.Left, 0, Simulation.PoseLeft)]
    [InlineData(PlayerAction.Right, 0, Simulation.PoseRight)]
    [InlineData(0, PlayerAction.Up, Simulation.PoseUp)]
    [InlineData(0, PlayerAction.Down, Simulation.PoseDown)]
    public void A_step_that_lands_faces_the_way_it_went(byte h, byte v, int pose)
    {
        var sim = Sim();
        var before = sim.PlayerCell;

        sim.PlayerTick(new PlayerAction(h, v, Fire: false));

        Assert.NotEqual(before, sim.PlayerCell);       // it really moved
        Assert.Equal(pose, sim.PlayerPose);
    }

    [Theory]
    [InlineData(PlayerAction.Left, Simulation.PoseFireLeft)]
    [InlineData(PlayerAction.Right, Simulation.PoseFireRight)]
    public void Firing_sideways_has_its_own_pose(byte h, int pose)
    {
        var sim = Sim();

        sim.PlayerTick(new PlayerAction(h, 0, Fire: true));

        Assert.Equal(pose, sim.PlayerPose);
    }

    [Theory]
    [InlineData(PlayerAction.Left, Simulation.PoseFireLeft)]
    [InlineData(PlayerAction.Right, Simulation.PoseFireRight)]
    public void A_blocked_sideways_step_is_drawn_reaching(byte h, int pose)
    {
        // Sideways is written twice. 0x1019B and 0x10318 face the way the step is
        // about to go, using the same pose firing that way uses, and only a step
        // that lands replaces it at 0x1030A or 0x10487. So walking into a wall
        // looks like reaching for it, which is exactly what firing looks like.
        var sim = Sim();
        var before = sim.PlayerCell;
        var delta = h == PlayerAction.Left ? -CaveGrid.CellStride : CaveGrid.CellStride;
        sim.Grid[before + delta] = RuntimeTile.Steel;

        sim.PlayerTick(new PlayerAction(h, 0, Fire: false));

        Assert.Equal(before, sim.PlayerCell);          // did not move
        Assert.Equal(pose, sim.PlayerPose);
    }

    [Theory]
    [InlineData(PlayerAction.Up, Simulation.PoseUp)]
    [InlineData(PlayerAction.Down, Simulation.PoseDown)]
    public void A_blocked_vertical_step_looks_the_same_as_one_that_lands(byte v, int pose)
    {
        // Vertical is written once, on entry, and never corrected - 0x10495 and
        // 0x10067 are the only writes. So up and down do NOT have the sideways
        // distinction, and a port that gave them one would be inventing.
        var sim = Sim();
        var before = sim.PlayerCell;
        var delta = v == PlayerAction.Up ? -CaveGrid.RowStride : CaveGrid.RowStride;
        sim.Grid[before + delta] = RuntimeTile.Steel;

        sim.PlayerTick(new PlayerAction(0, v, Fire: false));

        Assert.Equal(before, sim.PlayerCell);
        Assert.Equal(pose, sim.PlayerPose);
    }
}
