namespace Rockford.Engine;

/// <summary>
/// Puts a recorded session back through a fresh host.
/// </summary>
/// <remarks>
/// The whole value of this is that it is boring: the calls go back in the order
/// they came out, and nothing else happens. There is no state to restore and no
/// save format to invent, because the state IS the calls - which is the reason
/// #52 puts surviving a page reload behind this rather than in front of it.
///
/// A replay that does not reproduce the session is the interesting outcome and
/// must be loud. <see cref="Replay"/> therefore refuses a recording made
/// against different data rather than running it and reporting a divergence
/// that is really the wrong file.
/// </remarks>
public static class SessionPlayer
{
    /// <summary>
    /// Applies a recording to a host, and answers how far it got.
    /// </summary>
    /// <param name="recording">What was recorded.</param>
    /// <param name="host">A fresh host built on the same data.</param>
    /// <param name="identity">That data's identity.</param>
    /// <param name="upTo">Stop after this many calls, or all of them.</param>
    /// <param name="onCall">Called after each one, for an inspector.</param>
    /// <exception cref="InvalidOperationException">
    /// The recording was made against different data.
    /// </exception>
    public static int Replay(
        SessionRecording recording,
        GameHost host,
        SessionIdentity identity,
        int? upTo = null,
        Action<int, HostCall, GameHost>? onCall = null)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(host);

        // The engine version is deliberately NOT compared. A recording made on
        // an older engine that still replays identically is evidence the engine
        // did not move, which is the most useful thing this can be asked; a
        // version check would refuse exactly that experiment. The DATA has to
        // match, because different caves are not a divergence, they are a
        // different question.
        if (recording.Identity.CellmapsHash != identity.CellmapsHash)
        {
            throw new InvalidOperationException(
                $"this recording was made against different caves: "
                + $"{recording.Identity.CellmapsHash} against {identity.CellmapsHash}");
        }

        if (recording.Identity.TablesHash != identity.TablesHash)
        {
            throw new InvalidOperationException(
                $"this recording was made against different cave tables: "
                + $"{recording.Identity.TablesHash} against {identity.TablesHash}");
        }

        if (recording.Identity.WorldsHash != identity.WorldsHash)
        {
            throw new InvalidOperationException(
                $"this recording was made against a different world list: "
                + $"{recording.Identity.WorldsHash} against {identity.WorldsHash}");
        }

        // Recording while replaying would append the replayed calls to whatever
        // the caller had attached, doubling the session.
        host.Recorder = null;

        var limit = Math.Min(upTo ?? recording.Calls.Count, recording.Calls.Count);
        for (var i = 0; i < limit; i++)
        {
            var call = recording.Calls[i];
            Apply(call, host);
            onCall?.Invoke(i, call, host);
        }
        return limit;
    }

    private static void Apply(HostCall call, GameHost host)
    {
        switch (call.Kind)
        {
            case HostCallKind.Tick: host.Tick(call.Action); break;
            case HostCallKind.ToMenu: host.ToMenu(); break;
            case HostCallKind.MoveSelection: host.MoveSelection(call.A); break;
            case HostCallKind.StartSelectedWorld: host.StartSelectedWorld(); break;
            case HostCallKind.Load: host.Load(call.A, call.B); break;
            case HostCallKind.Go: host.Go(call.A); break;
            case HostCallKind.RestartCave: host.RestartCave(); break;
            case HostCallKind.AbandonToChooser: host.AbandonToChooser(); break;
            case HostCallKind.SetScreen: host.Screen = (GameScreen)call.A; break;
            case HostCallKind.SetSelected: host.Selected = call.A; break;

            // Not a default that shrugs. A recording from a future version
            // carrying a call this build cannot make is not something to skip
            // quietly - skipping it produces a replay that diverges for a
            // reason the output never mentions.
            default:
                throw new InvalidOperationException(
                    $"this build does not know how to replay {call.Kind}");
        }
    }
}
