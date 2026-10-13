using Rockford.Engine;

namespace Rockford.Tests;

// Lifted out of FingerprintTests on 19 September 2026 so the replay tests can
// use the same one. The function is unchanged, so every baseline in that file
// still holds - and two copies of a whole-state hash is exactly the thing that
// goes quietly out of step.
/// <summary>
/// FNV-1a over everything the simulation holds, rolled forward across steps so
/// the ORDER of the run is part of the answer as well as its end state.
///
/// FNV rather than a cryptographic hash because this is a change detector and
/// not a security boundary, and because it is short enough to read in a diff.
/// </summary>
internal sealed class Fingerprint
{
    private ulong _hash = 0xCBF29CE484222325;

    private void Feed(byte b)
    {
        _hash ^= b;
        _hash *= 0x100000001B3;
    }

    private void Feed(int value)
    {
        for (var i = 0; i < 4; i++) Feed((byte)(value >> (i * 8)));
    }

    private void Feed(bool value) => Feed((byte)(value ? 1 : 0));

    /// <summary>Everything, in a fixed order. Adding a field changes every number below.</summary>
    public void Absorb(Simulation sim)
    {
        var grid = sim.Grid;

        for (var y = 0; y < CaveGrid.Height; y++)
        for (var x = 0; x < CaveGrid.Width; x++)
        {
            var addr = CaveGrid.AddressOf(x, y);
            Feed(grid.ValueAt(addr));
            Feed(grid.StateAt(addr));

            // The parallel plane, which the player writes its second half into.
            Feed(grid.ValueAt(addr + CaveGrid.ShadowPlane));
            Feed(grid.StateAt(addr + CaveGrid.ShadowPlane));
        }

        Feed(sim.Random.Seed);
        Feed(sim.TickCounter);
        Feed(sim.TimeRemaining);
        Feed(sim.TreasureQuota);
        Feed(sim.PlayerCell);
        Feed(sim.PlayerPose);
        Feed(sim.PlayerDead);
        Feed(sim.DeathComplete);
        Feed(sim.CaveComplete);
        Feed(sim.TimeExpired);
        Feed(sim.TimedWallLatched);
        Feed(sim.TimedWallStart);
        Feed(sim.LastSoundedCell);
        Feed(sim.FootstepToggle);
        Feed(sim.SoundFlags);

        // Everything below was missing from the first version of this file and
        // is here because Astra pointed out that "everything" was not true.
        Feed(sim.TakeScoreDelta());
        Feed(sim.ProducerUpArmed);
        Feed(sim.ProducerDownArmed);
        Feed(sim.CaveHasAmoeba);
        Feed(sim.AmoebaCap);
        Feed(sim.Cheat);

        // The worms keep headings and lengths that outlive a cave, so a change
        // in them can show up caves later and nowhere near its cause. Their
        // Blocked and Reversed flags live on a private WormState and are not
        // reachable from here; the head, tail and heading are, and a change in
        // either flag moves those within a step or two.
        Feed(sim.WormALength);
        Feed(sim.WormAHead);
        Feed(sim.WormATail);
        Feed(sim.WormADirection);
        Feed(sim.WormBLength);
        Feed(sim.WormBHead);
        Feed(sim.WormBTail);
        Feed(sim.WormBDirection);

        // Drained, so the frames are part of the fingerprint rather than piling
        // up. A front end takes these every step and so does this.
        foreach (var frame in sim.TakeSoundFrames())
        {
            Feed((byte)frame.Event);
            Feed(frame.Divisor);
        }
    }

    /// <summary>The host's own state, for the trace that runs a whole session.</summary>
    public void Absorb(GameHost host)
    {
        Feed(host.Frame);
        Feed(host.Time);
        Feed(host.Collected);
        Feed(host.Needed);
        Feed(host.ExitOpen);
        Feed(host.HoldUntil);
        Feed((int)host.Screen);

        // WHICH CAVE, which the first version of this did not feed at all - so
        // a run that ended on the wrong cave hashed the same as one that did
        // not, and the replay test could not have seen a divergence that only
        // moved the stage. Astra found the gap by finding the defect it hid.
        Feed(host.World);
        Feed(host.Stage);
        Feed(host.Selected);

        Feed(host.Session.Lives);
        Feed(host.Session.Score);
        Feed(host.Session.IsOver);
        Feed(host.View.Column);
        Feed(host.View.Row);
    }

    public override string ToString() => _hash.ToString("x16");
}
