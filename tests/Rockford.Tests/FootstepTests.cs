using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// Walking, from the walk routine's tail at <c>0x11EF5</c>, and the handler that
/// serves it at <c>0x13443</c>.
///
/// The port never raised this request at all, so the game was silent as the
/// player moved. Its handler was there and had been written to draw from the
/// shared generator, which the original does for the boulder and the treasure but
/// not for this one - so adding the sound without fixing the handler would have
/// put a draw into the stream on every step the player took.
/// </summary>
public class FootstepTests
{
    /// <summary>A player with room to move, and steel beyond it.</summary>
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

    private static readonly PlayerAction Left = new(PlayerAction.Left, 0, Fire: false);
    private static readonly PlayerAction Right = new(PlayerAction.Right, 0, Fire: false);
    private static readonly PlayerAction Up = new(0, PlayerAction.Up, Fire: false);

    // ---- raising it ------------------------------------------------------

    /// <summary>
    /// Takes the arrival footstep out of the way, so a test can assert about
    /// walking rather than about being placed.
    /// </summary>
    private static Simulation Settled(ushort seed = 0)
    {
        var sim = Sim(seed);
        sim.PlayerTick(PlayerAction.Idle);
        sim.Step();
        return sim;
    }

    [Fact]
    public void Arriving_in_a_cave_sounds_a_footstep()
    {
        // ds:0x1DF8 is zero in the executable's image and nothing initialises it
        // at runtime - not the loader at 0x1094B and not the respawn at 0xFE70.
        // So the first tick finds the player somewhere other than address zero
        // and sounds a step before they have taken one. That is behaviour, not an
        // accident of uninitialised memory, and a port that quietly seeded the
        // remembered cell would lose it.
        var sim = Sim();

        sim.PlayerTick(PlayerAction.Idle);

        Assert.Equal(0x02, sim.SoundFlags & 0x02);
    }

    [Fact]
    public void A_step_that_lands_asks_for_the_sound()
    {
        var sim = Settled();

        sim.PlayerTick(Left);

        Assert.Equal(0x02, sim.SoundFlags & 0x02);
    }

    [Fact]
    public void A_step_into_a_wall_does_not()
    {
        // 0x11F04 compares the position, not whether a direction was pressed. The
        // player starts at column five with steel at column two, so three steps
        // land and the fourth does not.
        var sim = Settled();
        for (var i = 0; i < 3; i++) { sim.PlayerTick(Left); sim.Step(); }

        sim.PlayerTick(Left);

        Assert.Equal(0, sim.SoundFlags & 0x02);
    }

    [Fact]
    public void Standing_still_is_silent()
    {
        var sim = Settled();

        sim.PlayerTick(PlayerAction.Idle);

        Assert.Equal(0, sim.SoundFlags & 0x02);
    }

    [Fact]
    public void Firing_is_silent_because_it_does_not_move_anyone()
    {
        // 0x11F12 is a separate entry and never reaches the tail at all.
        var sim = Settled();

        sim.PlayerTick(new PlayerAction(PlayerAction.Left, 0, Fire: true));

        Assert.Equal(0, sim.SoundFlags & 0x02);
    }

    [Fact]
    public void Stepping_into_the_exit_is_silent()
    {
        // 0x11EFA: the cave being finished suppresses the footstep, so the
        // completion has the moment to itself.
        var sim = Settled();
        sim.Grid[CaveGrid.AddressOf(4, 5)] = RuntimeTile.ExitOpen;

        sim.PlayerTick(Left);

        Assert.True(sim.CaveComplete);
        Assert.Equal(0, sim.SoundFlags & 0x02);
    }

    [Fact]
    public void Every_step_asks_again()
    {
        // The request is cleared by the handler when it is served, and the walk
        // tail remembers the new position - so a player walking across a cave
        // gets a footstep per cell rather than one for the journey.
        var sim = Settled();
        var heard = 0;

        for (var i = 0; i < 4; i++)
        {
            sim.PlayerTick(i % 2 == 0 ? Left : Right);
            if ((sim.SoundFlags & 0x02) != 0) heard++;
            sim.Step();                       // the driver serves and clears it
        }

        Assert.Equal(4, heard);
    }

    // ---- the pitch -------------------------------------------------------

    [Fact]
    public void The_two_pitches_alternate_rather_than_being_drawn()
    {
        // 0x13453 tests a toggle byte and flips it. Its neighbours at 0x1341F and
        // 0x134E4 really do call the generator, which is why this one reads as
        // though it should.
        // Sim rather than Settled: the toggle starts at the image's zero, so the
        // first footstep SERVED is the lower pitch, and settling would have spent
        // it on the arrival step.
        var sim = Sim();
        var pitches = new List<int>();

        for (var i = 0; i < 6; i++)
        {
            sim.Request(SoundEvent.LowRumble);
            foreach (var frame in Frames(sim))
                if (frame.Event == SoundEvent.LowRumble) pitches.Add(frame.Divisor);
        }

        Assert.Equal([0x5800, 0x5000, 0x5800, 0x5000, 0x5800, 0x5000], pitches);
    }

    [Fact]
    public void Serving_a_footstep_costs_no_randomness()
    {
        // This is the one that matters. The footstep fires on every step the
        // player takes, so a draw here would move every later push, amoeba and
        // projectile decision in the cave.
        var sounding = Settled();
        var silent = Settled();

        for (var i = 0; i < 5; i++)
        {
            sounding.Request(SoundEvent.LowRumble);
            sounding.Step();
            silent.Step();
        }

        Assert.Equal(silent.Random.Seed, sounding.Random.Seed);
    }

    [Fact]
    public void Walking_costs_no_randomness_beyond_the_pose_draw()
    {
        // The whole point of porting the request rather than leaving it out: a
        // cave where the player walks has to leave the generator exactly where a
        // cave where they stood still and drew the same number of poses does.
        var walking = Settled();
        var standing = Settled();

        for (var i = 0; i < 4; i++)
        {
            walking.PlayerTick(i % 2 == 0 ? Left : Right);
            walking.Step();
            standing.PlayerTick(PlayerAction.Idle);
            standing.Step();
        }

        Assert.Equal(standing.Random.Seed, walking.Random.Seed);
    }

    [Fact]
    public void The_footstep_does_not_gag_the_sounds_above_it()
    {
        // 0x13443 sits below treasure in the chain, so a gem collected on the same
        // step as a step taken still speaks first.
        var sim = Settled();
        sim.Request(SoundEvent.TreasureFalling);
        sim.Request(SoundEvent.LowRumble);

        var frames = Frames(sim);

        Assert.Equal(SoundEvent.TreasureFalling, frames[0].Event);
        Assert.Contains(frames, f => f.Event == SoundEvent.LowRumble);
    }

    [Fact]
    public void The_pitch_sequence_survives_the_cave_being_rebuilt()
    {
        // ds:0x167C is a global and nothing resets it - not the loader, not the
        // respawn. A Simulation is one cave, so the host hands it over; without
        // that the sequence restarts on the lower pitch every cave and a death
        // silently re-tunes the next footstep.
        var first = Sim();
        first.Request(SoundEvent.LowRumble);
        first.Step();                                  // serves 0x5800

        var second = Sim();
        second.CarryGlobalsFrom(first);
        second.Request(SoundEvent.LowRumble);
        second.Step();

        Assert.Contains(second.TakeSoundFrames(),
                        f => f.Event == SoundEvent.LowRumble && f.Divisor == 0x5000);
    }

    [Fact]
    public void The_remembered_cell_survives_the_cave_being_rebuilt_too()
    {
        // The same global lifetime, and the reason it matters: a cave reloaded
        // after a death puts the player back where they started, and the original
        // remembers where the footstep last fired. Carrying both together is what
        // keeps the two in step.
        var first = Sim();
        first.PlayerTick(Left);

        var second = Sim();
        second.CarryGlobalsFrom(first);

        Assert.Equal(first.LastSoundedCell, second.LastSoundedCell);
    }

    [Fact]
    public void Walking_up_sounds_the_same_as_walking_sideways()
    {
        // All four directions fall through to the same tail - 0x11EBD and 0x11ED9
        // jump there rather than returning - so there is no direction that moves
        // the player silently.
        var sim = Settled();

        sim.PlayerTick(Up);

        Assert.Equal(0x02, sim.SoundFlags & 0x02);
    }

    private static IReadOnlyList<SoundFrame> Frames(Simulation sim)
    {
        sim.Step();
        return sim.TakeSoundFrames();
    }
}
