using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The fire tap, the acid tap and what they shoot, checked against <c>0x140AE</c>,
/// <c>0x14445</c>, <c>0x14322</c>, <c>0x1434E</c> and <c>0x14473</c>.
///
/// None of this existed in the engine before: the previous one had no notion of a
/// projectile at all, which is why several shipped caves were unsolvable in it.
/// </summary>
public class ProjectileTests
{
    private static CaveGrid Cave()
    {
        var g = new CaveGrid();
        for (var y = 1; y < CaveGrid.Height - 1; y++)
        for (var x = 1; x < CaveGrid.Width - 1; x++)
            g.WriteWord(x, y, 0xFF00 | RuntimeTile.Dirt);
        return g;
    }

    private static void Put(CaveGrid g, int x, int y, byte v) =>
        g.WriteWord(x, y, (ushort)(0xFF00 | v));

    private static void Put(CaveGrid g, int x, int y, byte v, byte state) =>
        g.WriteWord(x, y, (ushort)((state << 8) | v));

    /// <summary>
    /// Arms a tap the way the game does: stand next to it and shoot it. There is no
    /// back door for this, deliberately - going through the real path means these tests
    /// also cover the arming chain at <c>0x1200A</c>.
    /// </summary>
    private static Simulation Armed(CaveGrid g, int tapX, int tapY, ushort seed = 0)
    {
        Put(g, tapX - 1, tapY, RuntimeTile.Player);

        var sim = new Simulation(g, 600, randomSeed: seed) { PlayerCell = CaveGrid.AddressOf(tapX - 1, tapY) };
        sim.PlayerTick(PlayerAction.Shoot(PlayerAction.Right));
        return sim;
    }

    private static void ArmAlso(Simulation sim, CaveGrid g, int tapX, int tapY)
    {
        Put(g, tapX - 1, tapY, RuntimeTile.Player);
        sim.PlayerCell = CaveGrid.AddressOf(tapX - 1, tapY);
        sim.PlayerTick(PlayerAction.Shoot(PlayerAction.Right));
    }

    // ---- the taps ----

    [Fact]
    public void An_unarmed_tap_emits_nothing_however_long_you_wait()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.FireTap);
        Put(g, 5, 4, RuntimeTile.Space);

        var sim = new Simulation(g, 600);
        for (var i = 0; i < 40; i++) sim.Step();

        Assert.Equal(RuntimeTile.Space, g.ValueAt(5, 4));
    }

    [Fact]
    public void An_armed_fire_tap_emits_upward_into_empty_space()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.FireTap);
        Put(g, 5, 4, RuntimeTile.Space);

        var sim = Armed(g, 5, 5);
        for (var i = 0; i < 16 && g.ValueAt(5, 4) == RuntimeTile.Space; i++) sim.Step();

        Assert.Equal(RuntimeTile.FireHead, g.ValueAt(5, 4));
    }

    [Fact]
    public void An_armed_acid_tap_emits_downward()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.AcidTap);
        Put(g, 5, 6, RuntimeTile.Space);

        var sim = Armed(g, 5, 5);
        for (var i = 0; i < 16 && g.ValueAt(5, 6) == RuntimeTile.Space; i++) sim.Step();

        // One past the head. The tap writes the head into the cell below it, and the
        // scan is working downwards, so it reaches that cell later in the same step and
        // advances it a phase before the step is over.
        Assert.Equal((byte)(RuntimeTile.AcidHead + 1), g.ValueAt(5, 6));
    }

    [Fact]
    public void A_tap_cycles_its_four_animation_states_and_wraps()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.FireTap);
        Put(g, 5, 4, RuntimeTile.Steel);          // blocked, so only the animation runs

        var sim = Armed(g, 5, 5);

        // The sequence, not the set - the same hole the open exit's test had.
        // A tap stuck on one state, or one that cycled in a different order or at
        // a different rate, produced the same four values and passed.
        //
        // Unlike the exit this one has no tick gate: 0x140B4 advances on every
        // visit, so the four states come one a step and the fourth wraps.
        var seen = new List<byte>();
        for (var i = 0; i < 8; i++)
        {
            sim.Step();
            seen.Add(g.ValueAt(5, 5));
        }

        Assert.Equal([0x0D, 0x0E, 0x0F, 0x0C, 0x0D, 0x0E, 0x0F, 0x0C], seen);
    }

    /// <summary>
    /// The fire tap adds four to the counter before testing it and the acid tap does
    /// not, so two taps in the same cave never emit on the same step.
    /// </summary>
    [Fact]
    public void The_two_taps_emit_on_opposite_phases()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.FireTap);
        Put(g, 5, 4, RuntimeTile.Space);
        Put(g, 20, 5, RuntimeTile.AcidTap);
        Put(g, 20, 6, RuntimeTile.Space);

        var sim = Armed(g, 5, 5);
        ArmAlso(sim, g, 20, 5);

        int? fireStep = null, acidStep = null;
        for (var i = 0; i < 16; i++)
        {
            sim.Step();
            if (fireStep is null && g.ValueAt(5, 4) != RuntimeTile.Space) fireStep = i;
            if (acidStep is null && g.ValueAt(20, 6) != RuntimeTile.Space) acidStep = i;
            if (fireStep is not null && acidStep is not null) break;
        }

        Assert.NotNull(fireStep);
        Assert.NotNull(acidStep);

        // Direction matters, not just separation. The acid tap tests the counter
        // directly and so emits first; the fire tap adds four and follows.
        Assert.Equal(Simulation.FireTapPhase, fireStep!.Value - acidStep!.Value);
    }

    // ---- travel ----

    [Fact]
    public void Acid_falls_one_cell_per_chain_cycle()
    {
        var g = Cave();
        for (var y = 5; y <= 10; y++) Put(g, 5, y, RuntimeTile.Space);
        Put(g, 5, 5, RuntimeTile.AcidHead);

        var sim = new Simulation(g, 600);
        for (var i = 0; i < 5; i++) sim.Step();     // 1F 20 21 22 then 23 acts

        Assert.Equal(RuntimeTile.Space, g.ValueAt(5, 5));

        // Again one past the head: it moved down into a row the scan had not reached.
        Assert.Equal((byte)(RuntimeTile.AcidHead + 1), g.ValueAt(5, 6));
    }

    [Fact]
    public void Fire_rises_one_cell_per_chain_cycle()
    {
        var g = Cave();
        for (var y = 5; y <= 10; y++) Put(g, 5, y, RuntimeTile.Space);
        Put(g, 5, 10, RuntimeTile.FireHead);

        var sim = new Simulation(g, 600);
        for (var i = 0; i < 4; i++) sim.Step();     // 2C 2D 2E then 2F acts

        Assert.Equal(RuntimeTile.Space, g.ValueAt(5, 10));
        Assert.Equal(RuntimeTile.FireHead, g.ValueAt(5, 9));
    }

    // ---- the two rules that make them worth having ----

    /// <summary>
    /// From <c>0x1448A</c>: fire tests the cell above against the acid's whole state
    /// range and takes a kind-2 blast with it. Neither survives.
    /// </summary>
    [Fact]
    public void Fire_and_acid_destroy_each_other_from_the_second_acid_phase_on()
    {
        var g = Cave();
        for (var y = 4; y <= 10; y++) Put(g, 5, y, RuntimeTile.Space);

        Put(g, 5, 8, RuntimeTile.FireHead);
        Put(g, 5, 7, RuntimeTile.AcidRangeFirst);    // acid directly above

        var sim = new Simulation(g, 600);
        for (var i = 0; i < 4; i++) sim.Step();

        // A blast leaves its own phase values behind; what matters is that neither the
        // fire nor the acid is still there.
        Assert.NotEqual(RuntimeTile.FireHead, g.ValueAt(5, 8));
        Assert.False(g.ValueAt(5, 7) is >= RuntimeTile.AcidRangeFirst and <= RuntimeTile.AcidRangeLast);
    }

    /// <summary>
    /// Why the tested range starts at 20 and not at the acid head, 1F.
    ///
    /// The scan runs downwards, so the cell above fire is always visited before fire is.
    /// Acid sitting there at 1F has already been advanced to 20 by the time fire's
    /// terminal looks up at it. Fire can therefore never observe 1F above itself, and
    /// excluding it from the range costs nothing.
    ///
    /// Trying to write a test where fire meets a 1F is what surfaced this: the setup
    /// cannot be constructed. So this asserts the observable consequence instead.
    /// </summary>
    [Fact]
    public void Acid_placed_at_its_head_above_fire_has_advanced_by_the_time_fire_looks()
    {
        var g = Cave();
        for (var y = 4; y <= 10; y++) Put(g, 5, y, RuntimeTile.Space);
        Put(g, 5, 8, RuntimeTile.FireHead);
        Put(g, 5, 7, RuntimeTile.AcidHead);          // 1F at the start of the step

        var sim = new Simulation(g, 600);
        for (var i = 0; i < 4; i++) sim.Step();

        // It was 23 by the time fire's terminal ran, which is inside the range, so the
        // pair destroyed each other after all.
        Assert.NotEqual(RuntimeTile.FireHead, g.ValueAt(5, 8));
        Assert.False(g.ValueAt(5, 7) is >= RuntimeTile.AcidRangeFirst and <= RuntimeTile.AcidRangeLast);
    }

    /// <summary>
    /// From <c>0x1355E</c>. It does not destroy the treasure - it hides it behind a
    /// creature of the family that drops treasure when killed.
    /// </summary>
    [Fact]
    public void Treasure_struck_by_fire_becomes_the_treasure_yielding_creature()
    {
        var g = Cave();
        for (var y = 5; y <= 10; y++) Put(g, 5, y, RuntimeTile.Space);
        Put(g, 5, 8, RuntimeTile.FireHead);
        Put(g, 5, 7, RuntimeTile.Treasure);

        var sim = new Simulation(g, 600);
        for (var i = 0; i < 4; i++) sim.Step();

        Assert.Equal(RuntimeTile.EnemyClockwise, g.ValueAt(5, 7));
    }

    // ---- fire that cannot rise ----

    [Fact]
    public void Fire_blocked_above_goes_the_way_it_was_already_going()
    {
        var g = Cave();
        Put(g, 5, 8, RuntimeTile.FireHead, RuntimeTile.FireGoingLeft);
        Put(g, 4, 8, RuntimeTile.Space);
        // above stays dirt, so it cannot rise

        var sim = new Simulation(g, 600);
        for (var i = 0; i < 4; i++) sim.Step();

        Assert.Equal(RuntimeTile.Space, g.ValueAt(5, 8));
        Assert.Equal(RuntimeTile.FireHead, g.ValueAt(4, 8));
        Assert.Equal(RuntimeTile.FireGoingLeft, g.StateAt(4, 8));
    }

    /// <summary>
    /// Moving right writes the deferred seed rather than the live value, because the
    /// scan has not reached that cell yet and the fire would otherwise move twice in
    /// one step. Moving left writes the live value. This asymmetry is in the original.
    /// </summary>
    [Fact]
    public void Fire_moving_right_is_written_as_a_deferred_seed()
    {
        var g = Cave();
        Put(g, 5, 8, RuntimeTile.FireHead, RuntimeTile.FireGoingRight);
        Put(g, 6, 8, RuntimeTile.Space);

        var sim = new Simulation(g, 600);
        for (var i = 0; i < 4; i++) sim.Step();

        Assert.Equal(RuntimeTile.Space, g.ValueAt(5, 8));

        // The seed has already resolved: the scan reached the cell to the right later
        // in the same step and turned it into the head. That is the whole point of it.
        // Writing the head there directly would have left 2D instead, because the phase
        // chain would have advanced it, and the fire would have lost a frame.
        Assert.Equal(RuntimeTile.FireHead, g.ValueAt(6, 8));
        Assert.Equal(RuntimeTile.FireGoingRight, g.StateAt(6, 8));
    }

    [Fact]
    public void Fire_turns_around_when_its_own_direction_is_blocked()
    {
        var g = Cave();
        Put(g, 5, 8, RuntimeTile.FireHead, RuntimeTile.FireGoingLeft);
        // left is dirt, right is open
        Put(g, 6, 8, RuntimeTile.Space);

        var sim = new Simulation(g, 600);
        for (var i = 0; i < 4; i++) sim.Step();

        Assert.Equal(RuntimeTile.Space, g.ValueAt(5, 8));
        Assert.Equal(RuntimeTile.FireHead, g.ValueAt(6, 8));
        Assert.Equal(RuntimeTile.FireGoingRight, g.StateAt(6, 8));
    }

    /// <summary>
    /// Going left needs no seed, because the scan has already passed that cell, so the
    /// live head is written and survives the step untouched. The two directions are
    /// deliberately not symmetrical in the original and this pins the difference.
    /// </summary>
    [Fact]
    public void Fire_moving_left_is_written_live_and_is_not_advanced()
    {
        var g = Cave();
        Put(g, 5, 8, RuntimeTile.FireHead, RuntimeTile.FireGoingLeft);
        Put(g, 4, 8, RuntimeTile.Space);

        var sim = new Simulation(g, 600);
        for (var i = 0; i < 4; i++) sim.Step();

        Assert.Equal(RuntimeTile.FireHead, g.ValueAt(4, 8));
    }

    /// <summary>
    /// Boxed in on all three sides, fire draws and usually burns out. Over many
    /// attempts it must do both, or the draw is not being consulted.
    /// </summary>
    [Fact]
    public void Fire_boxed_in_usually_burns_out_but_sometimes_survives()
    {
        var survived = 0;
        var died = 0;

        for (ushort seed = 0; seed < 64; seed++)
        {
            var g = Cave();
            Put(g, 5, 8, RuntimeTile.FireHead, RuntimeTile.FireGoingLeft);
            // every neighbour is dirt

            var sim = new Simulation(g, 600, randomSeed: seed);
            for (var i = 0; i < 4; i++) sim.Step();

            if (g.ValueAt(5, 8) == RuntimeTile.FireHead) survived++;
            else died++;
        }

        Assert.True(survived > 0, "fire never survived, so the one-in-four branch is unreachable");
        Assert.True(died > survived, $"expected burning out to be the common case, got {died} to {survived}");
    }

    // ---- the deferred seeds ----

    [Theory]
    [InlineData(RuntimeTile.SeedAcidSettled, RuntimeTile.AcidSettled)]
    [InlineData(RuntimeTile.SeedFireHead, RuntimeTile.FireHead)]
    [InlineData(RuntimeTile.SeedAcidBlocked2, RuntimeTile.AcidBlocked2)]
    public void Every_dispatched_seed_becomes_its_real_value_on_the_next_visit(byte seed, byte becomes)
    {
        var g = Cave();
        Put(g, 5, 8, seed);

        new Simulation(g, 600).Step();

        Assert.Equal(becomes, g.ValueAt(5, 8));
    }

    /// <summary>
    /// F0, F1, F3 and F4 write only the value byte and leave the state alone; F2 writes
    /// the whole word and so resets the state to FF. Seeding with a distinctive state
    /// byte is the only way to tell the two instructions apart.
    /// </summary>
    [Fact]
    public void Only_the_F2_seed_overwrites_the_state_byte()
    {
        var g = Cave();
        Put(g, 5, 8, RuntimeTile.SeedFireHead, 0xAB);      // 0x145AF, byte write
        Put(g, 7, 8, RuntimeTile.SeedAcidSettled, 0xAB);   // 0x1443D, byte write
        Put(g, 9, 8, RuntimeTile.SeedAcidBlocked2, 0xAB);  // 0x146D0, WORD write

        new Simulation(g, 600).Step();

        Assert.Equal(0xAB, g.StateAt(5, 8));
        Assert.Equal(0xAB, g.StateAt(7, 8));
        Assert.Equal(0xFF, g.StateAt(9, 8));
    }

    /// <summary>
    /// F3 and F4 become the head of each worm, from <c>0x13ED4</c> and
    /// <c>0x1460F</c>. Both write the VALUE byte only, like every other seed.
    ///
    /// This test used to assert the opposite - that they were left alone - because
    /// A7 and B7 had no handler and resolving them would have produced a value the
    /// scan then ignored for the rest of the cave. It said in as many words that it
    /// would fail when the worms landed, and it did.
    /// </summary>
    [Theory]
    [InlineData(RuntimeTile.SeedA7, RuntimeTile.WormALast)]
    [InlineData(RuntimeTile.SeedB7, RuntimeTile.WormBLast)]
    public void A_worm_seed_becomes_the_head_it_names(byte seed, byte head)
    {
        var g = Cave();
        Put(g, 5, 8, seed, state: 0xAB);

        new Simulation(g, 600).Step();

        Assert.Equal(head, g.ValueAt(5, 8));
        Assert.Equal(0xAB, g.StateAt(5, 8));     // the value byte only
    }

    // ---- settled acid, which spreads sideways when it cannot fall ----

    [Fact]
    public void Settled_acid_crawls_along_whatever_is_holding_it_up()
    {
        var g = Cave();
        Put(g, 5, 8, RuntimeTile.AcidSettled, RuntimeTile.AcidGoingLeft);
        Put(g, 4, 8, RuntimeTile.Space);
        Put(g, 5, 9, RuntimeTile.Steel);                  // cannot fall

        var sim = new Simulation(g, 600);
        for (var i = 0; i < 4; i++) sim.Step();           // 28 29 2A then 2B acts

        Assert.Equal(RuntimeTile.Space, g.ValueAt(5, 8));
        Assert.Equal(RuntimeTile.AcidSettled, g.ValueAt(4, 8));
        Assert.Equal(RuntimeTile.AcidGoingLeft, g.StateAt(4, 8));
    }

    [Fact]
    public void Settled_acid_moving_right_uses_its_own_deferred_seed_not_fires()
    {
        var g = Cave();
        Put(g, 5, 8, RuntimeTile.AcidSettled, RuntimeTile.AcidGoingRight);
        Put(g, 6, 8, RuntimeTile.Space);
        Put(g, 5, 9, RuntimeTile.Steel);

        var sim = new Simulation(g, 600);
        for (var i = 0; i < 4; i++) sim.Step();           // 28 29 2A then 2B acts

        Assert.Equal(RuntimeTile.Space, g.ValueAt(5, 8));
        Assert.Equal(RuntimeTile.AcidSettled, g.ValueAt(6, 8));
        Assert.Equal(RuntimeTile.AcidGoingRight, g.StateAt(6, 8));
    }

    /// <summary>
    /// From <c>0x143A0</c>. Acid attacks a creature beside it, which fire never does -
    /// fire converts treasure instead. The blast is centred on the creature.
    /// </summary>
    [Fact]
    public void Settled_acid_kills_a_creature_beside_it()
    {
        var g = Cave();
        Put(g, 5, 8, RuntimeTile.AcidSettled, RuntimeTile.AcidGoingLeft);
        Put(g, 4, 8, RuntimeTile.EnemyClockwise);
        Put(g, 5, 9, RuntimeTile.Steel);

        var sim = new Simulation(g, 600);
        for (var i = 0; i < 4; i++) sim.Step();

        Assert.NotEqual(RuntimeTile.EnemyClockwise, g.ValueAt(4, 8));
    }

    /// <summary>
    /// The fallback to the opposite side tests space only. A creature that way is not
    /// attacked, which is where acid differs from its own preferred direction.
    /// </summary>
    [Fact]
    public void Settled_acid_does_not_attack_on_the_fallback_side()
    {
        var g = Cave();
        Put(g, 5, 8, RuntimeTile.AcidSettled, RuntimeTile.AcidGoingLeft);
        // left is dirt so the preferred side fails; a creature sits to the right
        Put(g, 6, 8, RuntimeTile.EnemyClockwise);
        Put(g, 5, 9, RuntimeTile.Steel);

        var sim = new Simulation(g, 600);
        for (var i = 0; i < 4; i++) sim.Step();

        Assert.Equal(RuntimeTile.EnemyClockwise, g.ValueAt(6, 8));
    }

    // ---- the random stream ----

    /// <summary>
    /// The port must draw exactly as often as the original, in the same places. An extra
    /// or missing draw shifts every later random event in the cave, which no behavioural
    /// assertion would catch. These compare the generator's state, not its output.
    /// </summary>
    [Fact]
    public void Fire_with_a_remembered_direction_draws_nothing()
    {
        var g = Cave();
        Put(g, 5, 8, RuntimeTile.FireHead, RuntimeTile.FireGoingLeft);
        Put(g, 4, 8, RuntimeTile.Space);

        var sim = new Simulation(g, 600, randomSeed: 0x1234);
        for (var i = 0; i < 4; i++) sim.Step();

        Assert.Equal(0x1234, sim.Random.Seed);
    }

    [Fact]
    public void Fire_with_no_remembered_direction_draws_exactly_once()
    {
        var g = Cave();
        Put(g, 5, 8, RuntimeTile.FireHead, 0x00);      // neither F8 nor FA
        Put(g, 4, 8, RuntimeTile.Space);
        Put(g, 6, 8, RuntimeTile.Space);

        var sim = new Simulation(g, 600, randomSeed: 0x1234);
        var expected = new GameRandom(0x1234);
        expected.Next();

        for (var i = 0; i < 4; i++) sim.Step();

        Assert.Equal(expected.Seed, sim.Random.Seed);
    }

    [Fact]
    public void Fire_boxed_in_draws_exactly_once_for_the_burnout()
    {
        var g = Cave();
        Put(g, 5, 8, RuntimeTile.FireHead, RuntimeTile.FireGoingLeft);
        // every neighbour is dirt, and the direction is remembered, so only the
        // burnout draw should happen

        var sim = new Simulation(g, 600, randomSeed: 0x1234);
        var expected = new GameRandom(0x1234);
        expected.Next();

        for (var i = 0; i < 4; i++) sim.Step();

        Assert.Equal(expected.Seed, sim.Random.Seed);
    }

    [Fact]
    public void Settled_acid_boxed_in_draws_exactly_once()
    {
        var g = Cave();
        Put(g, 5, 8, RuntimeTile.AcidSettled, RuntimeTile.AcidGoingLeft);
        Put(g, 5, 9, RuntimeTile.Steel);

        var sim = new Simulation(g, 600, randomSeed: 0x1234);
        var expected = new GameRandom(0x1234);
        expected.Next();

        for (var i = 0; i < 4; i++) sim.Step();

        Assert.Equal(expected.Seed, sim.Random.Seed);
    }
}
