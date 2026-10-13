using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The two worms - <c>0x1194B</c> and <c>0x11A68</c> for the movement, <c>0x13E78</c>
/// and <c>0x145B3</c> for the body.
///
/// Every worm here is hatched the way a cave hatches one: an authored egg is placed
/// and the simulation is stepped until the twenty seconds are up. That is slower than
/// writing the state in, and it is the point - it drives the same path a cave does,
/// and it keeps the random stream where a cave would leave it, which several of these
/// tests then measure.
/// </summary>
public class WormTests
{
    private const int Hatch = Simulation.TimedEntityHatchTicks / Simulation.TicksPerStep;

    /// <summary>An open room with one authored egg in it, stepped until it hatches.</summary>
    private static Simulation Hatched(
        byte egg, int x, int y, int direction, ushort seed = 0, bool wall = false)
    {
        var grid = new CaveGrid();

        // Space everywhere but the border, which the constructor already made steel.
        for (var ry = 1; ry < CaveGrid.Height - 1; ry++)
        for (var rx = 1; rx < CaveGrid.Width - 1; rx++)
            grid.WriteWord(CaveGrid.AddressOf(rx, ry), 0xFF00);

        var cell = CaveGrid.AddressOf(x, y);
        grid.WriteWord(cell, (ushort)(0xFF00 | egg));

        if (wall)
        {
            // Box the egg in, so the worm is stuck the moment it hatches.
            foreach (var d in new[] { -2, 2, -CaveGrid.RowStride, CaveGrid.RowStride })
                grid.WriteWord(cell + d, 0xFF02);
        }

        var sim = new Simulation(grid, 600, randomSeed: seed)
        {
            WormAHead = cell,
            WormBHead = cell,
            WormADirection = direction,
            WormBDirection = direction,
        };

        for (var i = 0; i < Hatch; i++) sim.Step();
        return sim;
    }

    private static Simulation WormA(int x, int y, int direction, ushort seed = 0, bool wall = false)
        => Hatched(RuntimeTile.TimedEntityA, x, y, direction, seed, wall);

    private static byte At(Simulation sim, int x, int y) => sim.Grid.ValueAt(CaveGrid.AddressOf(x, y));

    private static byte State(Simulation sim, int x, int y) => sim.Grid.StateAt(CaveGrid.AddressOf(x, y));

    // ---- it moves at all -------------------------------------------------

    [Fact]
    public void An_unhatched_worm_does_not_move()
    {
        // 0x1194B: the routine's first instruction is a test on the length, and a
        // zero length returns. Before this landed that was the whole of the worm -
        // the movement routines were not called at all, so an egg hatched into a
        // cell that then sat there for the rest of the cave.
        var sim = WormA(20, 10, Simulation.DirRight);
        var fresh = new Simulation(new CaveGrid(), 600);
        Assert.Equal(0, fresh.WormALength);

        fresh.Step();

        Assert.Equal(0, fresh.WormALength);
        Assert.Equal(0, fresh.WormAHead);
        Assert.Equal(1, sim.WormALength);            // the hatched one did get its length
    }

    [Fact]
    public void Hatching_leaves_the_head_where_the_egg_was()
    {
        // 0x13EE4 sets the length and nothing else. The head comes from the loader
        // at 0x109FB, which recorded the authored egg's cell - so a host that does
        // not pass CaveLoader.TimedEntityACell in gets a worm growing out of
        // whatever 0x10878 left, which is cell zero.
        var sim = WormA(20, 10, Simulation.DirRight);

        Assert.Equal(CaveGrid.AddressOf(20, 10), sim.WormAHead);
        Assert.Equal(RuntimeTile.WormA, At(sim, 20, 10));
        Assert.Equal(1, sim.WormALength);
    }

    // ---- the trail -------------------------------------------------------

    [Fact]
    public void The_first_move_leaves_a_tail_behind_and_a_head_in_front()
    {
        // 0x119B6 writes 0xF3 on the cell being left and 0x119E1 writes 0x4F on the
        // one being entered, and the scan resolves them to A7 and A0 the same pass.
        var sim = WormA(20, 10, Simulation.DirRight);

        sim.Step();

        Assert.Equal(RuntimeTile.WormALast, At(sim, 20, 10));   // A7, the tail
        Assert.Equal(RuntimeTile.WormA, At(sim, 21, 10));       // A0, the head
        Assert.Equal(2, sim.WormALength);
        Assert.Equal(CaveGrid.AddressOf(21, 10), sim.WormAHead);
        Assert.Equal(CaveGrid.AddressOf(20, 10), sim.WormATail);
    }

    [Fact]
    public void Resolving_the_two_seeds_leaves_the_facing_the_move_wrote()
    {
        // The move writes a facing on both cells, at 0x119DC and 0x119E4, and the
        // scan then resolves 0xF3 and 0x4F with 0x13ED4 and 0x13ED0 - both of which
        // write the VALUE BYTE ONLY. Resolve either with a whole word, which is the
        // obvious way to port a seed, and 0xFF goes over the facing.
        //
        // The two writes inside the move itself are byte-only in the original as
        // well, and there it makes no difference: each is followed by its own
        // facing a line later.
        var sim = WormA(20, 10, Simulation.DirRight);

        sim.Step();

        Assert.Equal(Simulation.FacingTag | Simulation.DirRight, State(sim, 20, 10));
        Assert.Equal(Simulation.FacingTag | Simulation.DirRight, State(sim, 21, 10));
    }

    [Fact]
    public void The_second_worm_keeps_its_facings_too()
    {
        // 0x1460F and 0x1460B, the other pair of seed resolutions. Worth its own
        // test rather than trusting the symmetry: the two worms are separate code
        // in the original and separate dispatch cases in the port, so one can be a
        // word write while the other is not.
        var sim = Hatched(RuntimeTile.TimedEntityB, 20, 10, Simulation.DirRight);

        sim.Step();

        Assert.Equal(RuntimeTile.WormBLast, At(sim, 20, 10));
        Assert.Equal(RuntimeTile.WormB, At(sim, 21, 10));
        Assert.Equal(Simulation.FacingTag | Simulation.DirRight, State(sim, 20, 10));
        Assert.Equal(Simulation.FacingTag | Simulation.DirRight, State(sim, 21, 10));
    }

    [Fact]
    public void The_body_ages_one_segment_a_step()
    {
        // 0x13EBB, the whole of the ordinary case. A0 is the newest cell, and each
        // scan moves every segment one further from the head.
        var sim = WormA(20, 10, Simulation.DirRight);

        sim.Step();
        sim.Step();
        sim.Step();

        Assert.Equal([0xA7, 0xA2, 0xA1, 0xA0],
                     new[] { At(sim, 20, 10), At(sim, 21, 10), At(sim, 22, 10), At(sim, 23, 10) });
        Assert.Equal(4, sim.WormALength);
    }

    [Fact]
    public void The_oldest_cell_is_pinned_at_the_last_segment()
    {
        // 0x13EC1: a cell that ages past A7 is erased UNLESS it is the one the tail
        // pointer names, which is put back to A7. Without that the worm would eat
        // its own tail eight steps in and shrink while its counted length grew.
        var sim = WormA(20, 10, Simulation.DirRight);

        for (var i = 0; i < 6; i++) sim.Step();

        Assert.Equal(RuntimeTile.WormALast, At(sim, 20, 10));
        Assert.Equal(7, sim.WormALength);
    }

    [Fact]
    public void It_stops_growing_at_eight_and_erases_a_cell_a_step()
    {
        // 0x119C4 caps the length and 0x119CA erases the cell the tail pointer
        // names. The pointer is not advanced there - 0x13EB7 does that, when the
        // cell behind passes through A6 on its way to A7.
        var sim = WormA(10, 10, Simulation.DirRight);

        // The seventh move is the one that matters. It takes the length to eight,
        // which is NOT past the cap, so nothing is erased and the worm is drawn at
        // its full eight for the first time. Test only the steady state and a cap
        // written one too tight passes: from the eighth move on the two are
        // identical, and all this catches is the worm never reaching its length.
        for (var i = 0; i < 7; i++) sim.Step();

        Assert.Equal(Simulation.MaxLength, sim.WormALength);
        Assert.Equal([0xA7, 0xA6, 0xA5, 0xA4, 0xA3, 0xA2, 0xA1, 0xA0], Body(sim, 10));
        Assert.Equal(CaveGrid.AddressOf(10, 10), sim.WormATail);

        // From here it erases one behind for every one it lays.
        for (var i = 0; i < 5; i++) sim.Step();

        Assert.Equal(Simulation.MaxLength, sim.WormALength);
        Assert.Equal([0xA7, 0xA6, 0xA5, 0xA4, 0xA3, 0xA2, 0xA1, 0xA0], Body(sim, 15));
        Assert.Equal(RuntimeTile.Space, At(sim, 14, 10));
        Assert.Equal(CaveGrid.AddressOf(15, 10), sim.WormATail);

        // 0x119CA is a WORD write, so the facing the worm left on that cell goes
        // with it. The body handler's own expiry at 0x13ECD is a byte write and
        // leaves the state alone - two clears of the same cell value, two widths,
        // and the port has to keep them apart.
        Assert.Equal(0xFF, State(sim, 14, 10));
    }

    /// <summary>Eight cells of row ten, oldest first.</summary>
    private static byte[] Body(Simulation sim, int from)
    {
        var body = new byte[8];
        for (var i = 0; i < 8; i++) body[i] = At(sim, from + i, 10);
        return body;
    }

    // ---- turning ---------------------------------------------------------

    [Fact]
    public void A_blocked_worm_tries_one_side_then_the_other_and_never_straight_on()
    {
        // 0x1197F and 0x11988 both reload ds:0x1610 before turning, so the two
        // attempts are one to each side of the heading. A worm facing a wall with
        // its left blocked goes right.
        var sim = WormA(20, 10, Simulation.DirRight);
        sim.Grid.WriteWord(CaveGrid.AddressOf(21, 10), 0xFF02);   // ahead: steel
        sim.Grid.WriteWord(CaveGrid.AddressOf(20, 9), 0xFF02);    // above: steel

        sim.Step();

        Assert.Equal(CaveGrid.AddressOf(20, 11), sim.WormAHead);  // so it went down
        Assert.Equal(Simulation.DirDown, sim.WormADirection);
    }

    [Fact]
    public void Which_side_it_tries_first_comes_from_the_generator()
    {
        // 0x11978 draws and 0x1197B tests bit zero: set falls through to 0x11A44,
        // which is the anticlockwise turn, and clear jumps to 0x11A56, the clockwise
        // one. With both sides of the worm clear the draw is the only thing
        // deciding, so the heading it comes out with reads the bit straight back.
        var ups = 0;

        for (ushort seed = 0; seed < 16; seed++)
        {
            var expected = (new GameRandom(seed).Next() & 1) != 0
                ? Simulation.DirUp                    // 0x11A44, dir - 1
                : Simulation.DirDown;                 // 0x11A56, dir + 1

            Assert.Equal(expected, Turned(seed));
            if (expected == Simulation.DirUp) ups++;
        }

        // Both branches have to be in those sixteen, or the assertion above would
        // hold for a port that picked one side and never drew at all.
        Assert.InRange(ups, 1, 15);
    }

    private static int Turned(ushort seed)
    {
        var sim = WormA(20, 10, Simulation.DirRight);
        sim.Grid.WriteWord(CaveGrid.AddressOf(21, 10), 0xFF02);
        sim.Random.Seed = seed;
        sim.Step();
        return sim.WormADirection;
    }

    // ---- reversing -------------------------------------------------------

    [Fact]
    public void With_nowhere_to_go_it_turns_round_and_crawls_back()
    {
        // 0x119F4: head and tail change ends. The worm does not reverse a direction,
        // it reverses which end of itself is the front, so it retraces exactly the
        // cells it laid rather than pushing into whatever boxed it in.
        var sim = Cornered();

        var head = sim.WormAHead;
        var tail = sim.WormATail;

        sim.Step();

        Assert.Equal(tail, sim.WormAHead);
        Assert.Equal(head, sim.WormATail);
    }

    [Fact]
    public void Reversing_mirrors_every_segment_in_one_scan()
    {
        // 0x13E7F to 0x13E99. The two ends are named outright and everything between
        // them is 0xA0 + length - 1 - value.
        //
        // Five cells, not three. A three-cell worm has one interior segment and it
        // maps to itself, so the arithmetic can be deleted outright and a
        // three-cell reversal still passes - Astra found that by mutation. At five
        // the interior is A1 A2 A3, which has to come back A3 A2 A1.
        var sim = Cornered(length: 5);
        Assert.Equal(5, sim.WormALength);
        Assert.Equal([0xA7, 0xA3, 0xA2, 0xA1, 0xA0], Segments(sim, 20, 5));

        sim.Step();

        Assert.Equal([0xA0, 0xA1, 0xA2, 0xA3, 0xA7], Segments(sim, 20, 5));
    }

    private static byte[] Segments(Simulation sim, int from, int count)
    {
        var cells = new byte[count];
        for (var i = 0; i < count; i++) cells[i] = At(sim, from + i, 10);
        return cells;
    }

    [Fact]
    public void Reversing_turns_every_facing_round_as_well()
    {
        // 0x13E9E: add 2, clear bit two, put the tag back - which is (dir + 2) & 3.
        var sim = Cornered();
        Assert.Equal(Simulation.FacingTag | Simulation.DirRight, State(sim, 21, 10));

        sim.Step();

        foreach (var x in new[] { 20, 21, 22 })
            Assert.Equal(Simulation.FacingTag | Simulation.DirLeft, State(sim, x, 10));
    }

    [Fact]
    public void Hatching_does_not_touch_the_head_pointer()
    {
        // 0x13EE0 and 0x13EE4 write the cell and the length, and stop. Nothing there
        // stores si into ds:0x1614 - the head was set much earlier, by the loader.
        //
        // Testing that with the head already at the egg proves nothing, because
        // writing it again would write the same value. So this puts the two in
        // different places, which is what a cave with a worm carried over from the
        // last one looks like for exactly one step.
        var grid = new CaveGrid();
        for (var ry = 1; ry < CaveGrid.Height - 1; ry++)
        for (var rx = 1; rx < CaveGrid.Width - 1; rx++)
            grid.WriteWord(CaveGrid.AddressOf(rx, ry), 0xFF00);

        var egg = CaveGrid.AddressOf(20, 10);
        var elsewhere = CaveGrid.AddressOf(5, 5);
        grid.WriteWord(egg, 0xFF00 | RuntimeTile.TimedEntityA);

        var sim = new Simulation(grid, 600) { WormAHead = elsewhere };
        for (var i = 0; i < Hatch; i++) sim.Step();

        Assert.Equal(RuntimeTile.WormA, sim.Grid.ValueAt(egg));
        Assert.Equal(elsewhere, sim.WormAHead);
    }

    [Fact]
    public void A_worm_that_has_reversed_goes_back_to_ageing_on_the_next_step()
    {
        // 0x11953 and 0x11958 clear both flags at the top of every step, before
        // anything can set them again. Leave them set and a worm that reverses once
        // mirrors its body on every scan for the rest of the cave - which looks
        // almost right, because a body mirrored twice is the body you started with,
        // and is why this needs asserting rather than eyeballing.
        var sim = Cornered();
        sim.Step();                                   // the reversal

        Assert.Equal([0xA0, 0xA1, 0xA7],
                     new[] { At(sim, 20, 10), At(sim, 21, 10), At(sim, 22, 10) });

        // It is now heading somewhere random from 0x11A0B. Point it back along its
        // own body, which is clear, so this step is an ordinary move.
        sim.WormADirection = Simulation.DirLeft;
        sim.Step();

        // Aged, not mirrored: the old head has moved on to A1 and a new A0 is in
        // front of it. A stuck flag left set would have frozen all three instead.
        Assert.Equal([0xA1, 0xA2, 0xA7],
                     new[] { At(sim, 20, 10), At(sim, 21, 10), At(sim, 22, 10) });
        Assert.Equal(RuntimeTile.WormA, At(sim, 19, 10));
        Assert.Equal(4, sim.WormALength);
    }

    /// <summary>A worm of the given length along row ten from column 20, with the
    /// only way on walled off.</summary>
    private static Simulation Cornered(int length = 3)
    {
        var sim = WormA(20, 10, Simulation.DirRight);
        for (var i = 1; i < length; i++) sim.Step();

        var head = CaveGrid.AddressOf(19 + length, 10);
        foreach (var d in new[] { 2, -CaveGrid.RowStride, CaveGrid.RowStride })
            sim.Grid.WriteWord(head + d, 0xFF02);

        return sim;
    }

    [Fact]
    public void A_worm_with_no_body_to_turn_into_freezes_instead()
    {
        // 0x11A03 sets the other flag, and 0x13EB1 makes the body handler return on
        // it. So a worm boxed in before it ever moved does not age its one cell -
        // which matters, because ageing it with nothing behind would run it to A8
        // and erase the worm.
        var sim = WormA(20, 10, Simulation.DirRight, wall: true);

        Assert.Equal(RuntimeTile.WormA, At(sim, 20, 10));
        Assert.Equal(0, sim.WormATail);

        // Every step, not just the last. Left to age, the cell reaches A6, claims
        // the tail pointer on the way past, and then flips between A0 and A7 as the
        // worm reverses into itself - so a single assertion at step twenty lands on
        // whichever of the two that parity gives and can pass by luck.
        for (var i = 0; i < 20; i++)
        {
            sim.Step();

            Assert.Equal(RuntimeTile.WormA, At(sim, 20, 10));
            Assert.Equal(0, sim.WormATail);
            Assert.Equal(1, sim.WormALength);
        }
    }

    [Fact]
    public void A_worm_dug_out_of_a_box_starts_ageing_again()
    {
        // The other half of 0x11953 - 0x11958 clears the stuck flag, and that one is
        // only observable on a worm that was stuck with no body and then got free.
        // Leave it set and the worm crawls away trailing a body that never ages,
        // so every cell behind it stays A0 and the worm is drawn as a solid bar.
        var sim = WormA(20, 10, Simulation.DirRight, wall: true);
        sim.Step();
        Assert.Equal(0, sim.WormATail);                   // stuck, not reversed

        // Let it out, the way a boulder falling through the wall would.
        sim.Grid.WriteWord(CaveGrid.AddressOf(21, 10), 0xFF00);
        sim.Grid.WriteWord(CaveGrid.AddressOf(22, 10), 0xFF00);
        sim.WormADirection = Simulation.DirRight;

        sim.Step();
        sim.Step();

        Assert.Equal([0xA7, 0xA1, 0xA0],
                     new[] { At(sim, 20, 10), At(sim, 21, 10), At(sim, 22, 10) });
    }

    // ---- what it does to what it meets -----------------------------------

    [Fact]
    public void The_evil_worm_walks_treasure_back_to_a_boulder()
    {
        // 0x11A1C writes 0xFF3C, the first value of a chain the scan was already
        // walking. It does not write a boulder; four scans later the chain's
        // terminal does.
        var sim = WormA(20, 10, Simulation.DirRight);
        sim.Grid.WriteWord(CaveGrid.AddressOf(21, 10), 0xFF40);
        sim.Grid.WriteWord(CaveGrid.AddressOf(21, 10) + CaveGrid.ShadowPlane, 0x1234);

        sim.Step();

        // Written as 0x3C, then aged once by the same scan.
        Assert.Equal(0x3D, At(sim, 21, 10));
        Assert.Equal(0xFFFF, sim.Grid.ReadWord(CaveGrid.AddressOf(21, 10) + CaveGrid.ShadowPlane));

        // 0x3D, 0x3E, 0x3F, and the third scan's dispatch on 0x3F writes the
        // boulder. A fourth would find it in mid-air and start it falling.
        for (var i = 0; i < 3; i++) sim.Step();
        Assert.Equal(RuntimeTile.Boulder, At(sim, 21, 10));
    }

    [Fact]
    public void The_evil_worm_turns_a_boulder_into_an_enemy_one_time_in_eight()
    {
        // 0x11A32 draws and 0x11A35 tests the low three bits against zero. The enemy
        // it makes is 0x90, the anticlockwise family - the one that explodes into
        // nothing.
        //
        // Counting how many of sixty-four seeds convert is not enough on its own:
        // any residue of eight gives the same count. So this names the seeds, by
        // asking an independent generator what the draw was.
        var converted = 0;

        for (ushort seed = 0; seed < 64; seed++)
        {
            var expected = (new GameRandom(seed).Next() & 7) == 0;

            var sim = WormA(20, 10, Simulation.DirRight);
            sim.Grid.WriteWord(CaveGrid.AddressOf(21, 10), 0xFF44);
            sim.Random.Seed = seed;

            sim.Step();

            // 0x38 written, then aged once by the same scan.
            Assert.Equal(expected, At(sim, 21, 10) == 0x39);
            if (expected) converted++;
        }

        Assert.Equal(8, converted);
    }

    [Fact]
    public void Both_worms_treat_a_falling_thing_the_same_as_a_resting_one()
    {
        // 0x11A16, 0x11A2C and 0x11B43 each test the falling value as well as the
        // resting one - 0x42 beside 0x40, and 0x46 beside 0x44. Test only the
        // resting values and the second comparison of every pair can be deleted,
        // which would leave a worm walking straight past anything mid-air.
        var treasure = WormA(20, 10, Simulation.DirRight);
        treasure.Grid.WriteWord(CaveGrid.AddressOf(21, 10), 0xFF42);
        treasure.Step();
        Assert.Equal(0x3D, At(treasure, 21, 10));

        var boulder = WormA(20, 10, Simulation.DirRight);
        boulder.Grid.WriteWord(CaveGrid.AddressOf(21, 10), 0xFF46);
        boulder.Random.Seed = FirstConvertingSeed();
        boulder.Step();
        Assert.Equal(0x39, At(boulder, 21, 10));

        var good = Good(0xFF46);
        good.Step();
        Assert.Equal(0x18, At(good, 21, 10));
    }

    /// <summary>The lowest seed whose first draw passes the one in eight.</summary>
    private static ushort FirstConvertingSeed()
    {
        for (ushort seed = 0; seed < 64; seed++)
            if ((new GameRandom(seed).Next() & 7) == 0) return seed;

        throw new Xunit.Sdk.XunitException("no seed in 64 rolled the one in eight");
    }

    [Fact]
    public void The_enemy_chain_finishes_as_the_anticlockwise_family()
    {
        var sim = ConvertsBoulder();

        // 0x39 already, then 0x3A, 0x3B, and the dispatch on 0x3B. A fourth step
        // and the enemy has walked off the cell being asserted.
        for (var i = 0; i < 3; i++) sim.Step();

        Assert.Equal(RuntimeTile.EnemyAnticlockwise, At(sim, 21, 10));
    }

    [Fact]
    public void The_good_worm_undoes_both_of_them()
    {
        // 0x11B33 walks 0x90 back to a boulder and 0x11B49 walks a boulder on to
        // treasure - the same two chains the first worm walks the other way.
        var enemy = Good(0xFF00 | RuntimeTile.EnemyAnticlockwise);
        for (var i = 0; i < 4; i++) enemy.Step();     // 0x35, 0x36, 0x37, dispatch
        Assert.Equal(RuntimeTile.Boulder, At(enemy, 21, 10));

        var boulder = Good(0xFF44);
        for (var i = 0; i < 4; i++) boulder.Step();   // 0x18, 0x19, 0x1A, dispatch
        Assert.Equal(RuntimeTile.Treasure, At(boulder, 21, 10));
    }

    [Fact]
    public void The_good_worm_converts_every_boulder_and_not_one_in_eight()
    {
        // 0x11B3F to 0x11B49 has no call to the generator in it. The two routines
        // are otherwise the same instruction for instruction, which is exactly why
        // this is worth a test: the asymmetry looks like a porting slip and is not.
        for (ushort seed = 0; seed < 8; seed++)
        {
            var sim = Good(0xFF44);
            sim.Random.Seed = seed;
            sim.Step();

            Assert.Equal(0x18, At(sim, 21, 10));      // 0x17 written, then aged once
        }
    }

    private static Simulation Good(int ahead)
    {
        var sim = Hatched(RuntimeTile.TimedEntityB, 20, 10, Simulation.DirRight);
        sim.Grid.WriteWord(CaveGrid.AddressOf(21, 10), (ushort)ahead);
        return sim;
    }

    // ---- the random stream -----------------------------------------------

    [Fact]
    public void Walking_into_clear_space_draws_nothing()
    {
        // 0x11973 jumps straight to the move. Neither the transform nor the turn
        // draw happens, so a worm crossing an empty cave leaves the stream alone.
        var sim = WormA(20, 10, Simulation.DirRight, seed: 0x1234);
        sim.Random.Seed = 0x4321;

        sim.Step();

        Assert.Equal(0x4321, sim.Random.Seed);
    }

    [Fact]
    public void Meeting_a_boulder_takes_two_numbers_and_meeting_a_wall_takes_one()
    {
        // The transform runs BEFORE the turn draw, at 0x11975 and 0x11978, and only
        // the boulder branch of it draws. So the count depends on what is in front,
        // and every later random event in the cave depends on the count.
        Assert.Equal(1, DrawsAgainst(0xFF02));        // steel: the turn draw only
        Assert.Equal(1, DrawsAgainst(0xFF40));        // treasure: transform, no draw
        Assert.Equal(2, DrawsAgainst(0xFF44));        // boulder: transform draws too
    }

    [Fact]
    public void The_good_worm_takes_one_number_for_a_boulder_and_not_two()
    {
        // The count, not just the outcome. Its transform at 0x11B2F has no call to
        // the generator anywhere in it, so a boulder costs it the turn draw and
        // nothing more - and a draw added there would leave the whole cave's later
        // random events one number out while every worm test still passed.
        Assert.Equal(1, DrawsAgainst(0xFF44, good: true));
        Assert.Equal(1, DrawsAgainst(0xFF02, good: true));
    }

    private static int DrawsAgainst(ushort ahead, bool good = false)
    {
        var sim = good
            ? Hatched(RuntimeTile.TimedEntityB, 20, 10, Simulation.DirRight)
            : WormA(20, 10, Simulation.DirRight);
        sim.Grid.WriteWord(CaveGrid.AddressOf(21, 10), ahead);
        sim.Random.Seed = 0;

        sim.Step();

        // Advance an independent generator from the same seed until it catches up.
        var reference = new GameRandom(0);
        for (var n = 1; n <= 4; n++)
        {
            reference.Next();
            if (reference.Seed == sim.Random.Seed) return n;
        }

        return -1;
    }

    [Fact]
    public void Being_stuck_draws_a_new_direction()
    {
        // 0x11A08, the third of the three places this routine can draw. It happens
        // whether the worm turned round or could not move at all.
        var sim = WormA(20, 10, Simulation.DirRight, wall: true);
        sim.Random.Seed = 0;

        sim.Step();

        var reference = new GameRandom(0);
        reference.Next();                              // the turn-order draw
        var expected = reference.Next() & 3;           // the new direction

        Assert.Equal(expected, sim.WormADirection);
        Assert.Equal(reference.Seed, sim.Random.Seed);
    }

    // ---- the two of them together ----------------------------------------

    [Fact]
    public void The_two_worms_run_in_order_and_keep_their_own_state()
    {
        // 0xEDBC then 0xEDBF, two calls with two sets of globals. A cave can hold
        // one of each and they do not share a length, a head or a heading.
        var grid = new CaveGrid();
        for (var ry = 1; ry < CaveGrid.Height - 1; ry++)
        for (var rx = 1; rx < CaveGrid.Width - 1; rx++)
            grid.WriteWord(CaveGrid.AddressOf(rx, ry), 0xFF00);

        var a = CaveGrid.AddressOf(10, 5);
        var b = CaveGrid.AddressOf(10, 15);
        grid.WriteWord(a, 0xFF00 | RuntimeTile.TimedEntityA);
        grid.WriteWord(b, 0xFF00 | RuntimeTile.TimedEntityB);

        var sim = new Simulation(grid, 600)
        {
            WormAHead = a,
            WormBHead = b,
            WormADirection = Simulation.DirRight,
            WormBDirection = Simulation.DirLeft,
        };

        for (var i = 0; i < Hatch + 3; i++) sim.Step();

        Assert.Equal(4, sim.WormALength);
        Assert.Equal(4, sim.WormBLength);
        Assert.Equal(RuntimeTile.WormA, At(sim, 13, 5));
        Assert.Equal(RuntimeTile.WormB, At(sim, 7, 15));
        Assert.Equal(RuntimeTile.WormALast, At(sim, 10, 5));
        Assert.Equal(RuntimeTile.WormBLast, At(sim, 10, 15));
    }

    [Fact]
    public void The_first_worm_draws_before_the_second()
    {
        // 0xEDBC then 0xEDBF, in that order. Two worms crossing an empty cave draw
        // nothing and the order cannot be seen - which is how a test of two worms
        // far apart passes with the calls swapped.
        //
        // Give them both a wall to turn at and they compete for the same stream:
        // the first takes the next number and the second takes the one after. A
        // seed where those two numbers disagree in bit zero sends them to opposite
        // sides, and swapping the calls swaps the sides.
        var seed = SeedWhereTheFirstTwoDrawsDiffer();

        var grid = new CaveGrid();
        for (var ry = 1; ry < CaveGrid.Height - 1; ry++)
        for (var rx = 1; rx < CaveGrid.Width - 1; rx++)
            grid.WriteWord(CaveGrid.AddressOf(rx, ry), 0xFF00);

        var a = CaveGrid.AddressOf(10, 5);
        var b = CaveGrid.AddressOf(10, 15);
        grid.WriteWord(a, 0xFF00 | RuntimeTile.TimedEntityA);
        grid.WriteWord(b, 0xFF00 | RuntimeTile.TimedEntityB);
        grid.WriteWord(a + CaveGrid.CellStride, 0xFF02);
        grid.WriteWord(b + CaveGrid.CellStride, 0xFF02);

        var sim = new Simulation(grid, 600)
        {
            WormAHead = a,
            WormBHead = b,
            WormADirection = Simulation.DirRight,
            WormBDirection = Simulation.DirRight,
        };

        for (var i = 0; i < Hatch; i++) sim.Step();
        sim.Random.Seed = seed;

        sim.Step();

        var reference = new GameRandom(seed);
        var first = (reference.Next() & 1) != 0 ? Simulation.DirUp : Simulation.DirDown;
        var second = (reference.Next() & 1) != 0 ? Simulation.DirUp : Simulation.DirDown;

        Assert.NotEqual(first, second);                // the seed was chosen for this
        Assert.Equal(first, sim.WormADirection);
        Assert.Equal(second, sim.WormBDirection);
    }

    private static ushort SeedWhereTheFirstTwoDrawsDiffer()
    {
        for (ushort seed = 0; seed < 256; seed++)
        {
            var r = new GameRandom(seed);
            if ((r.Next() & 1) != (r.Next() & 1)) return seed;
        }

        throw new Xunit.Sdk.XunitException("no seed in 256 gave two draws differing in bit zero");
    }

    [Fact]
    public void The_heading_is_the_one_thing_that_outlives_the_cave()
    {
        // 0xFEBA clears the length and 0x10878 the head and tail; ds:0x1610 and
        // ds:0x1618 are left alone, so a worm hatches facing wherever the last one
        // was facing. CarryGlobalsFrom is what a host rebuilding the cave uses.
        var first = new Simulation(new CaveGrid(), 600) { WormADirection = Simulation.DirDown };
        var second = new Simulation(new CaveGrid(), 600);

        second.CarryGlobalsFrom(first);

        Assert.Equal(Simulation.DirDown, second.WormADirection);
    }

    [Fact]
    public void A_heading_the_game_cannot_produce_is_refused()
    {
        // 0x11969 shifts the whole heading and indexes ds:0x1656 with it, without
        // masking - so a 4 would read the zero word behind the table. The two turns
        // mask with 3 and so does 0x11A0B, so the game never writes one. Masking on
        // the read would be a silent difference from the original; this keeps the
        // state unreachable instead.
        var sim = new Simulation(new CaveGrid(), 600);

        Assert.Throws<ArgumentOutOfRangeException>(() => sim.WormADirection = 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.WormBDirection = -1);
    }

    private static Simulation ConvertsBoulder()
    {
        for (ushort seed = 0; seed < 64; seed++)
        {
            var sim = WormA(20, 10, Simulation.DirRight);
            sim.Grid.WriteWord(CaveGrid.AddressOf(21, 10), 0xFF44);
            sim.Random.Seed = seed;
            sim.Step();

            if (At(sim, 21, 10) == 0x39) return sim;
        }

        throw new Xunit.Sdk.XunitException("no seed in 64 rolled the one in eight");
    }
}
