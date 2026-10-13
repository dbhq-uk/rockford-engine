using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The read-only observation surface: what it reports, and the boundary it keeps.
///
/// <para>
/// That it does not CHANGE anything is not asserted here. It is asserted in
/// <see cref="FingerprintTests"/>, whose numbers were captured from the engine
/// before any of this existed - which is a stronger statement than any assertion
/// in this file could make.
/// </para>
/// </summary>
public class ObservationTests
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

    private static (int Column, int Row) At(int address) =>
        (CaveGrid.ColumnOf(address), CaveGrid.RowOf(address));

    [Fact]
    public void A_boulder_falling_reports_where_it_came_from_and_where_it_went()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.Boulder);
        Put(g, 5, 6, RuntimeTile.Space);
        Put(g, 5, 7, RuntimeTile.Space);

        var sim = new Simulation(g, 600);

        // The first step only commits to the fall. Nothing has moved, so nothing is
        // reported - which is the point of the one-step delay and the thing a
        // renderer must not smooth over.
        sim.Step();
        Assert.Empty(sim.TakeObservations());

        sim.Step();

        var move = Assert.Single(sim.TakeObservations());
        Assert.Equal(ObservationKind.Moved, move.Kind);
        Assert.Equal((5, 5), At(move.From));
        Assert.Equal((5, 6), At(move.To));
        Assert.Equal(RuntimeTile.Boulder, move.Value);
    }

    [Fact]
    public void And_reports_the_landing_when_it_stops()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.Boulder);
        Put(g, 5, 6, RuntimeTile.Space);
        // Row 7 is dirt, so it falls one cell and stops on it.

        var sim = new Simulation(g, 600);
        sim.Step();             // commit
        sim.Step();             // move into row 6
        sim.TakeObservations();

        sim.Step();             // land

        var landed = Assert.Single(sim.TakeObservations());
        Assert.Equal(ObservationKind.Landed, landed.Kind);
        Assert.Equal((5, 6), At(landed.To));

        // Landing is not a move, so both ends are the same cell. A renderer reading
        // From as a source would otherwise slide the sprite in from nowhere.
        Assert.Equal(landed.From, landed.To);
    }

    [Fact]
    public void A_roll_off_a_shoulder_is_reported_as_the_sideways_move_it_is()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.Boulder);
        Put(g, 5, 6, RuntimeTile.Boulder);      // the shoulder
        Put(g, 4, 5, RuntimeTile.Space);
        Put(g, 4, 6, RuntimeTile.Space);

        var sim = new Simulation(g, 600);
        sim.Step();

        // Left is tried first, at 0x13F26. The lower boulder is resting on dirt and
        // does nothing, so the only move is the upper one rolling off it.
        var move = Assert.Single(sim.TakeObservations(),
            o => o.Kind == ObservationKind.Moved && At(o.From) == (5, 5));
        Assert.Equal((4, 5), At(move.To));
    }

    [Fact]
    public void The_player_walking_reports_the_step()
    {
        var g = Cave();
        Put(g, 10, 10, RuntimeTile.Player);

        var sim = new Simulation(g, 600) { PlayerCell = CaveGrid.AddressOf(10, 10) };
        sim.PlayerTick(PlayerAction.Walk(PlayerAction.Right));
        sim.Step();

        var move = Assert.Single(sim.TakeObservations(),
            o => o.Kind == ObservationKind.Moved && o.Value == RuntimeTile.Player);
        Assert.Equal((10, 10), At(move.From));
        Assert.Equal((11, 10), At(move.To));
    }

    [Fact]
    public void A_direction_held_against_steel_reports_nothing()
    {
        var g = Cave();
        Put(g, 1, 10, RuntimeTile.Player);      // hard against the left border

        var sim = new Simulation(g, 600) { PlayerCell = CaveGrid.AddressOf(1, 10) };
        sim.PlayerTick(PlayerAction.Walk(PlayerAction.Left));
        sim.Step();

        Assert.DoesNotContain(sim.TakeObservations(),
            o => o.Kind == ObservationKind.Moved && o.Value == RuntimeTile.Player);
    }

    [Fact]
    public void Firing_is_reported_separately_from_an_explosion()
    {
        var g = Cave();
        Put(g, 10, 10, RuntimeTile.Player);

        var sim = new Simulation(g, 600) { PlayerCell = CaveGrid.AddressOf(10, 10) };
        sim.PlayerTick(PlayerAction.Shoot(PlayerAction.Right));
        sim.Step();

        var seen = sim.TakeObservations();

        // The cell fired AT, not the player's own. This is the distinction the sound
        // stream cannot make: one request serves firing and an explosion in the
        // original, so a front end shaking the screen on SoundEvent.Blast would
        // shake it on every shot.
        var fired = Assert.Single(seen, o => o.Kind == ObservationKind.Fired);
        Assert.Equal((11, 10), At(fired.To));

        Assert.DoesNotContain(seen, o => o.Kind == ObservationKind.Blasted);

        // And firing does not move anyone.
        Assert.DoesNotContain(seen,
            o => o.Kind == ObservationKind.Moved && o.Value == RuntimeTile.Player);
    }

    [Fact]
    public void A_blast_is_reported_at_its_centre_rather_than_its_corner()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.Boulder);
        Put(g, 5, 6, RuntimeTile.Space);
        Put(g, 5, 7, RuntimeTile.Player);

        var sim = new Simulation(g, 600) { PlayerCell = CaveGrid.AddressOf(5, 7) };
        sim.Step();                             // commit to the fall
        sim.Step();                             // move into row 6
        sim.TakeObservations();

        sim.Step();                             // onto the player

        var blast = Assert.Single(sim.TakeObservations(), o => o.Kind == ObservationKind.Blasted);

        // The handler is passed the upper-left corner of the 3 by 3 block, which is
        // (4, 5). What blew up is the player at (5, 7)... one row below the boulder
        // that was at (5, 6), so the centre is (5, 7).
        Assert.Equal((5, 7), At(blast.To));

        // Kind 2 leaves space behind, which is what a boulder landing on the player
        // does. A falling GEM on the player is kind 1.
        Assert.Equal(2, blast.Value);
    }

    [Fact]
    public void Taking_them_drains_them()
    {
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.Boulder);
        Put(g, 5, 6, RuntimeTile.Space);
        Put(g, 5, 7, RuntimeTile.Space);

        var sim = new Simulation(g, 600);
        sim.Step();
        sim.Step();

        Assert.NotEmpty(sim.TakeObservations());
        Assert.Empty(sim.TakeObservations());
    }

    [Fact]
    public void What_the_player_did_survives_the_scan_that_follows_it()
    {
        // The trap this surface was designed around. GameHost calls PlayerTick and
        // THEN Step, so a buffer cleared at the top of Step would throw away the
        // player's own move on every single step of the game. Draining on take is
        // what makes the order impossible to get wrong.
        var g = Cave();
        Put(g, 10, 10, RuntimeTile.Player);
        Put(g, 20, 4, RuntimeTile.Boulder);
        Put(g, 20, 5, RuntimeTile.Space);
        Put(g, 20, 6, RuntimeTile.Space);

        var sim = new Simulation(g, 600) { PlayerCell = CaveGrid.AddressOf(10, 10) };
        sim.Step();                             // let the boulder commit to its fall
        sim.TakeObservations();

        sim.PlayerTick(PlayerAction.Walk(PlayerAction.Right));
        sim.Step();

        var seen = sim.TakeObservations();

        // Both, from the one drain: the player's step made before the scan, and the
        // boulder's made during it.
        Assert.Contains(seen, o => o.Kind == ObservationKind.Moved && o.Value == RuntimeTile.Player);
        Assert.Contains(seen, o => o.Kind == ObservationKind.Moved && o.Value == RuntimeTile.Boulder);
    }

    [Fact]
    public void A_walk_into_a_cell_that_is_then_blasted_still_reports_the_move()
    {
        // The engine's job is to report what happened, and the player DID move. It
        // is the renderer's job to notice the cell no longer holds the player. This
        // test pins the engine half of that, and RuntimeTile.SameFamily is what the
        // component uses to catch it.
        var g = Cave();
        Put(g, 5, 5, RuntimeTile.BoulderFalling);
        Put(g, 5, 6, RuntimeTile.Space);
        Put(g, 4, 6, RuntimeTile.Player);
        Put(g, 5, 7, RuntimeTile.Space);

        var sim = new Simulation(g, 600) { PlayerCell = CaveGrid.AddressOf(4, 6) };
        sim.PlayerTick(PlayerAction.Walk(PlayerAction.Right));
        sim.Step();

        var seen = sim.TakeObservations();
        var dest = CaveGrid.AddressOf(5, 6);

        var move = Assert.Single(seen,
            o => o.Kind == ObservationKind.Moved && o.To == dest);
        Assert.Equal(RuntimeTile.Player, move.Value);
        Assert.Contains(seen, o => o.Kind == ObservationKind.Blasted);

        // And the cell does NOT hold the player any more. Sliding whatever is here
        // back towards (4,6) would animate an explosion walking sideways.
        Assert.NotEqual(RuntimeTile.Player, g.ValueAt(dest));
        Assert.False(RuntimeTile.SameFamily(move.Value, g.ValueAt(dest)),
            "the family filter would have let a destroyed move through");
    }

    [Fact]
    public void A_family_match_spans_a_thing_s_states_and_rejects_everything_else()
    {
        // The reason the check is by family and not by value: a boulder reported
        // moving is Boulder, and the cell it lands in holds one of the falling
        // states, because that is what the scan order writes.
        Assert.True(RuntimeTile.SameFamily(RuntimeTile.Boulder, RuntimeTile.BoulderFalling));
        Assert.True(RuntimeTile.SameFamily(RuntimeTile.Boulder, RuntimeTile.BoulderFallingDeferred));
        Assert.True(RuntimeTile.SameFamily(RuntimeTile.Treasure, RuntimeTile.TreasureFallingDeferred));
        Assert.True(RuntimeTile.SameFamily(RuntimeTile.Player, RuntimeTile.PlayerShadow));

        Assert.False(RuntimeTile.SameFamily(RuntimeTile.Boulder, RuntimeTile.Treasure));
        Assert.False(RuntimeTile.SameFamily(RuntimeTile.Player, RuntimeTile.SpaceBlast2));
        Assert.False(RuntimeTile.SameFamily(RuntimeTile.EnemyClockwise, RuntimeTile.EnemyAnticlockwise));

        // Family zero is everything unclassified, and it never matches - not even
        // itself - so an unknown value snaps rather than animating on a coincidence.
        Assert.False(RuntimeTile.SameFamily(RuntimeTile.Dirt, RuntimeTile.Dirt));
        Assert.False(RuntimeTile.SameFamily(RuntimeTile.Space, RuntimeTile.Space));
    }

    [Fact]
    public void A_boulder_shoved_by_walking_reports_its_own_move()
    {
        // The player's step was reported from the start; the rock they shove was
        // not, so it snapped while the player sliding into its place did not. The
        // two are touching, which made it the most visible instance there is.
        var g = Cave();
        Put(g, 10, 10, RuntimeTile.Player);
        Put(g, 11, 10, RuntimeTile.Boulder);
        Put(g, 12, 10, RuntimeTile.Space);

        // The push is a coin flip on the generator, so try until it lands. Each
        // attempt is a fresh cave, because a successful push changes the layout.
        var pushed = false;
        for (ushort seed = 0; seed < 40 && !pushed; seed++)
        {
            var grid = Cave();
            Put(grid, 10, 10, RuntimeTile.Player);
            Put(grid, 11, 10, RuntimeTile.Boulder);
            Put(grid, 12, 10, RuntimeTile.Space);

            var sim = new Simulation(grid, 600, randomSeed: seed)
            {
                PlayerCell = CaveGrid.AddressOf(10, 10),
            };
            sim.PlayerTick(PlayerAction.Walk(PlayerAction.Right));
            sim.Step();

            foreach (var o in sim.TakeObservations())
            {
                if (o.Kind != ObservationKind.Moved || o.Value != RuntimeTile.Boulder) continue;
                Assert.Equal((11, 10), At(o.From));
                Assert.Equal((12, 10), At(o.To));
                pushed = true;
            }
        }

        Assert.True(pushed, "no seed in forty produced a successful push");
    }

    [Fact]
    public void An_enemy_moving_reports_it()
    {
        // Every enemy move in the game goes through one method, so this covers both
        // families and all four directions.
        var g = Cave();
        for (var x = 4; x <= 12; x++)
        for (var y = 4; y <= 12; y++)
            Put(g, x, y, RuntimeTile.Space);

        Put(g, 8, 8, RuntimeTile.EnemyClockwise);

        var sim = new Simulation(g, 600);
        var moved = false;

        for (var i = 0; i < 12 && !moved; i++)
        {
            sim.Step();
            foreach (var o in sim.TakeObservations())
                if (o.Kind == ObservationKind.Moved
                    && RuntimeTile.SameFamily(o.Value, RuntimeTile.EnemyClockwise))
                    moved = true;
        }

        Assert.True(moved, "an enemy crossed open space and never reported a move");
    }

    [Fact]
    public void A_host_that_never_drains_is_capped_rather_than_unbounded()
    {
        // Not a state the game reaches - GameHost drains every tick. It is here so
        // that a front end which stops calling cannot turn a read-only window into
        // a leak for the life of a session.
        var g = Cave();
        for (var x = 3; x < 37; x++)
        {
            Put(g, x, 3, RuntimeTile.Boulder);
            for (var y = 4; y < 20; y++) Put(g, x, y, RuntimeTile.Space);
        }

        var sim = new Simulation(g, 6000);
        for (var i = 0; i < 400; i++) sim.Step();

        Assert.True(sim.TakeObservations().Count <= 512,
            "the buffer grew past its cap");
    }
}
