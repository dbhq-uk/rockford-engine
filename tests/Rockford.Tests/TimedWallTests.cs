using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The timed converting wall, from its own handler at <c>0x1467F</c> and the two
/// sites that open its window - <c>0x13F65</c> for treasure and <c>0x141B8</c> for
/// a boulder.
///
/// The port never dispatched the wall's own range at all, so a wall something had
/// fallen into sat on its first cell for the rest of the cave instead of cycling.
/// </summary>
public class TimedWallTests
{
    /// <summary>A cave with one wall cell and space around it.</summary>
    private static Simulation Sim()
    {
        var grid = new CaveGrid();
        for (var y = 3; y <= 9; y++)
            for (var x = 3; x <= 9; x++)
                grid[CaveGrid.AddressOf(x, y)] = RuntimeTile.Space;

        var sim = new Simulation(grid, 600, randomSeed: 1);
        sim.Grid.WriteWord(Wall, 0xFF00 | RuntimeTile.TimedWall);
        return sim;
    }

    private static readonly int Wall = CaveGrid.AddressOf(5, 6);

    private static byte WallValue(Simulation sim) => sim.Grid.ValueAt(Wall);

    // ---- the handler -----------------------------------------------------

    [Fact]
    public void A_wall_nothing_has_touched_does_not_animate()
    {
        // 0x14684: no latch, no animation. The wall only comes alive once
        // something has fallen into one.
        var sim = Sim();

        for (var i = 0; i < 10; i++) sim.Step();

        Assert.Equal(RuntimeTile.TimedWall, WallValue(sim));
    }

    [Fact]
    public void A_live_wall_cycles_through_its_four_cells()
    {
        // 0x14692 increments, 0x14698 wraps past the last one back to the first.
        // This is what tells the player the wall is live, and the port showed
        // none of it.
        var sim = Sim();
        sim.TimedWallLatched = true;

        var seen = new List<byte>();
        for (var i = 0; i < 6; i++) { sim.Step(); seen.Add(WallValue(sim)); }

        Assert.Equal([0x75, 0x76, 0x77, 0x74, 0x75, 0x76], seen);
    }

    [Fact]
    public void Past_the_window_it_stops_animating_and_stays_a_wall()
    {
        // The audit's point: there is no conversion to brick at 0x1467F, and no
        // latching shut either. It simply stops moving.
        var sim = Sim();
        sim.TimedWallLatched = true;
        sim.TimedWallStart = -Simulation.TimedWallTicks - 60;   // the window is past

        for (var i = 0; i < 10; i++) sim.Step();

        Assert.Equal(RuntimeTile.TimedWall, WallValue(sim));
    }

    // 0x14694's wrap sits outside the window test, so on a reading of the listing
    // alone a cell already past the last of the four should come back whether the
    // wall is live or not. There is no test for that because it cannot happen: the
    // only way to reach 0xFF78 is the increment two instructions earlier, which
    // needs the window open - and a cell authored at 0x78 is a timed entity and
    // dispatches somewhere else entirely. The placement is faithful and has no
    // observable consequence.

    // ---- the arithmetic --------------------------------------------------

    [Fact]
    public void The_elapsed_compare_is_sixteen_bits_and_signed()
    {
        // `sub ax,ds:0x1cf4` then `cmp ax,0xe10` with a SIGNED jump. A cave that
        // runs past 32,768 ticks - nine minutes, which a 999 second limit allows -
        // wraps the difference negative and the window opens again.
        //
        // An int subtraction would have this wall dead for the rest of the cave.
        var sim = Sim();
        sim.TimedWallLatched = true;
        sim.TimedWallStart = 0;

        // Past the window, so it is shut.
        while (sim.InterruptTicks <= Simulation.TimedWallTicks) sim.Step();
        var shut = WallValue(sim);
        sim.Step();
        Assert.Equal(shut, WallValue(sim));

        // And past the sign bit, where it opens again.
        while (sim.InterruptTicks < 0x8000) sim.Step();
        var before = WallValue(sim);
        sim.Step();

        Assert.NotEqual(before, WallValue(sim));
    }

    // ---- the window, and what it converts --------------------------------

    [Fact]
    public void A_boulder_falling_in_opens_the_window_for_every_wall()
    {
        // 0x13F65 is a single global latch, not one per wall.
        var sim = Sim();
        Assert.False(sim.TimedWallLatched);

        sim.Grid.WriteWord(Wall - CaveGrid.RowStride, 0xFF00 | RuntimeTile.Boulder);
        sim.Step();

        Assert.True(sim.TimedWallLatched);
    }

    [Fact]
    public void Every_cave_starts_with_the_wall_inactive()
    {
        // The loader clears the latch:
        //
        //   108a4: mov BYTE PTR ds:0x1696,0x0
        //
        // and every route back into it goes through there - cave completion at
        // 0xEE50 and death at 0xEE93 both return via 0xED9A to 0xFE03, which calls
        // the loader at 0xFE4D. So a wall the last cave brought to life is dead
        // again in this one until something falls into it.
        var first = Sim();
        first.TimedWallLatched = true;
        first.TimedWallStart = 500;

        var second = Sim();
        second.CarryGlobalsFrom(first);
        for (var i = 0; i < 10; i++) second.Step();

        Assert.False(second.TimedWallLatched);
        Assert.Equal(RuntimeTile.TimedWall, WallValue(second));
    }

    [Fact]
    public void The_timestamp_carries_even_though_the_latch_does_not()
    {
        // ds:0x1CF4 is written at 0x13F74 and 0x141C7 and cleared nowhere, so it
        // survives the cave the way the footstep globals do. Nothing can read it
        // before the next activation overwrites it, which is exactly why the
        // asymmetry is safe rather than a bug - but the port should still be the
        // shape the binary is.
        var first = Sim();
        first.TimedWallStart = 500;

        var second = Sim();
        second.CarryGlobalsFrom(first);

        Assert.Equal(500, second.TimedWallStart);
    }
}
