using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The two dormant timed entities, from <c>0x13ED8</c> and <c>0x14613</c>.
///
/// Authored <c>0x70</c> is the evil worm and authored <c>0x84</c> the good one, and
/// each spends its first twenty seconds as a four-frame animation before hatching.
/// The port had the arming and neither the timer nor the handler, so an authored
/// worm sat on its first frame for the whole cave and nothing ever came out of it.
/// </summary>
public class TimedEntityTests
{
    private static readonly int Cell = CaveGrid.AddressOf(5, 5);

    /// <summary>A room with one dormant entity in it.</summary>
    private static Simulation Sim(byte entity)
    {
        var grid = new CaveGrid();
        for (var y = 3; y <= 8; y++)
            for (var x = 3; x <= 8; x++)
                grid[CaveGrid.AddressOf(x, y)] = RuntimeTile.Space;

        var sim = new Simulation(grid, 600);
        sim.Grid.WriteWord(Cell, (ushort)(0xFF00 | entity));
        return sim;
    }

    private static byte Value(Simulation sim) => sim.Grid.ValueAt(Cell);

    // ---- the timers ------------------------------------------------------

    [Fact]
    public void The_interrupt_runs_four_counters_and_not_two()
    {
        // 0x1337C to 0x1338B increments ds:0x16A8, 0x16AA, 0x16AC and 0x226C on
        // every interrupt. The port had the first and the last, so the two worm
        // timers never moved and nothing that depends on them could ever fire.
        var sim = Sim(RuntimeTile.TimedEntityA);

        sim.Step();

        Assert.Equal(Simulation.TicksPerStep, sim.InterruptTicks);
        Assert.Equal(Simulation.TicksPerStep, sim.TimedEntityATimer);
        Assert.Equal(Simulation.TicksPerStep, sim.TimedEntityBTimer);
    }

    // ---- the dormant animation -------------------------------------------

    [Theory]
    [InlineData(RuntimeTile.TimedEntityA, 0x78)]
    [InlineData(RuntimeTile.TimedEntityB, 0x7C)]
    public void A_dormant_entity_cycles_its_four_frames(byte entity, byte first)
    {
        // 0x13EEC: increment, wrap past the fourth. The value byte only - whatever
        // the loader left in the state byte has to survive, and a whole-word
        // increment would carry into it.
        var sim = Sim(entity);
        sim.Grid[Cell + 1] = 0xAB;

        var seen = new List<byte>();
        for (var i = 0; i < 6; i++) { sim.Step(); seen.Add(Value(sim)); }

        Assert.Equal(
            [(byte)(first + 1), (byte)(first + 2), (byte)(first + 3),
             first, (byte)(first + 1), (byte)(first + 2)],
            seen);
        Assert.Equal(0xAB, sim.Grid.StateAt(Cell));
    }

    // ---- hatching --------------------------------------------------------

    [Theory]
    [InlineData(RuntimeTile.TimedEntityA, RuntimeTile.WormA)]
    [InlineData(RuntimeTile.TimedEntityB, RuntimeTile.WormB)]
    public void It_hatches_on_its_own_after_twenty_seconds(byte entity, byte worm)
    {
        // 1200 interrupts at 60 Hz. Nobody has to touch it: the interrupt counts
        // for it, so a cave left alone produces a worm.
        var sim = Sim(entity);

        var steps = Simulation.TimedEntityHatchTicks / Simulation.TicksPerStep;
        for (var i = 0; i < steps - 1; i++) sim.Step();
        Assert.NotEqual(worm, Value(sim));

        sim.Step();

        Assert.Equal(worm, Value(sim));
    }

    [Theory]
    [InlineData(RuntimeTile.TimedEntityA, RuntimeTile.WormA)]
    [InlineData(RuntimeTile.TimedEntityB, RuntimeTile.WormB)]
    public void Touching_one_wakes_it_at_once(byte entity, byte worm)
    {
        // 0x1013A writes 1200 outright when the player walks into it or fires at
        // it. The handler tests BELOW 1200, so writing exactly that hatches it on
        // the next scan. Touching the thing is the fast way to a worm rather than
        // a way to stop one, which is worth knowing before you try it.
        var sim = Sim(entity);
        sim.PlayerCell = CaveGrid.AddressOf(4, 5);
        sim.Grid[sim.PlayerCell] = RuntimeTile.Player;

        sim.PlayerTick(new PlayerAction(PlayerAction.Right, 0, Fire: false));
        sim.Step();

        Assert.Equal(worm, Value(sim));
    }

    [Fact]
    public void Hatching_writes_the_whole_cell_and_starts_the_worm_at_one()
    {
        // 0x13EE0 writes the WORD 0xFFA0, unlike the animation above it, and
        // 0x13EE4 sets the length to one.
        var sim = Sim(RuntimeTile.TimedEntityA);
        sim.Grid[Cell + 1] = 0xAB;
        Assert.Equal(0, sim.WormALength);

        var steps = Simulation.TimedEntityHatchTicks / Simulation.TicksPerStep;
        for (var i = 0; i < steps; i++) sim.Step();

        Assert.Equal(RuntimeTile.WormA, Value(sim));
        Assert.Equal(0xFF, sim.Grid.StateAt(Cell));
        Assert.Equal(1, sim.WormALength);
    }

    [Fact]
    public void The_two_entities_keep_their_own_time()
    {
        // Separate counters, so touching one does not hatch the other.
        var sim = Sim(RuntimeTile.TimedEntityB);
        var other = CaveGrid.AddressOf(7, 5);
        sim.Grid.WriteWord(other, 0xFF00 | RuntimeTile.TimedEntityA);

        sim.PlayerCell = CaveGrid.AddressOf(6, 5);
        sim.Grid[sim.PlayerCell] = RuntimeTile.Player;
        sim.PlayerTick(new PlayerAction(PlayerAction.Right, 0, Fire: false));
        sim.Step();

        Assert.Equal(RuntimeTile.WormA, sim.Grid.ValueAt(other));
        Assert.Equal(RuntimeTile.TimedEntityB + 1, Value(sim));   // still counting
    }

    // ---- the terminal that makes a creature -------------------------------

    [Fact]
    public void Runtime_three_b_becomes_an_anticlockwise_enemy_facing_left()
    {
        // 0x1413A calls 0x1356C, which writes 0x90 with facing 0xF8 and clears the
        // shadow. Every other terminal in that run produces a boulder or a gem;
        // this is the one that produces a creature, and it was not dispatched.
        //
        // 0x90 is the ANTICLOCKWISE family. This test asserted the clockwise one
        // when it landed, matching a wrong constant in the dispatch - the two turn
        // opposite ways and explode into different things, so it was not cosmetic.
        var sim = Sim(RuntimeTile.TimedEntityA);
        var cell = CaveGrid.AddressOf(7, 7);
        sim.Grid.WriteWord(cell, 0xFF3B);

        sim.Step();

        Assert.Equal(RuntimeTile.EnemyAnticlockwise, sim.Grid.ValueAt(cell));
        Assert.Equal(Simulation.FacingTag | Simulation.DirLeft, sim.Grid.StateAt(cell));
    }
}
