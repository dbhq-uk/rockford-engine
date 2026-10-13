using System.Text.Json;
using Rockford.Engine;

namespace Rockford.Cli;

/// <summary>
/// Walks a recorded session and says what happened.
///
/// READ ONLY, and the point of it. It supplies the recorded calls to a fresh
/// host and reports what came back; it cannot change a session, and there is no
/// way to ask it to. A tool that can edit a replay is a tool whose output is no
/// longer evidence of anything.
///
/// What it answers is "why did that happen", which nothing here could answer
/// before. A death was a thing you watched once. Now it has a call number, the
/// action that was supplied, the cell the player was in, and the state of the
/// cave on the step before - and the call number is a place to come back to as
/// many times as it takes.
/// </summary>
public static class Inspector
{
    /// <summary>
    /// Replays a recording against the data in a directory.
    /// </summary>
    /// <param name="recordingPath">The recording to walk.</param>
    /// <param name="gameDir">Where cellmaps.bin, caves.json and worlds.json are.</param>
    /// <param name="at">Stop here and report in full, or null to walk it all.</param>
    public static int Run(string recordingPath, string gameDir, int? at)
    {
        if (!File.Exists(recordingPath))
        {
            Console.Error.WriteLine($"No recording at {recordingPath}.");
            return 2;
        }

        var recording = SessionRecordingFile.Load(recordingPath);
        if (recording is null)
        {
            Console.Error.WriteLine($"{recordingPath} is not a recording.");
            return 2;
        }

        if (!TryLoadData(gameDir, out var cellmaps, out var tables, out var worlds))
            return 2;

        // worlds.json lists all ten, but a build hands its host only the worlds it
        // can draw, and the recording pinned THAT list. So replay against whichever
        // build's list the recording was made on. Neither matching falls through to
        // the faithful build's, and the refusal below says why.
        worlds = WorldList.AsRecorded(worlds, recording.Identity.WorldsHash);

        var identity = SessionIdentity.Of(cellmaps, tables, worlds);
        var host = new GameHost(cellmaps, tables, worlds);

        Console.WriteLine($"{recording.Calls.Count} calls, recorded on engine "
                          + $"{recording.Identity.Engine}");
        Console.WriteLine($"caves  {recording.Identity.CellmapsHash}   "
                          + $"tables {recording.Identity.TablesHash}");
        Console.WriteLine();

        try
        {
            SessionPlayer.Replay(recording, host, identity, at,
                (i, call, h) => Report(i, call, h, at));
        }
        catch (InvalidOperationException refused)
        {
            // The whole reason the identity is in the file. Say which, rather
            // than letting a divergence be mistaken for an engine fault.
            Console.Error.WriteLine($"Refused: {refused.Message}");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// One line a call, and everything at the one being asked about.
    /// </summary>
    /// <remarks>
    /// Quiet by default, because a session is tens of thousands of ticks and a
    /// line each is not a report, it is a haystack. What gets a line is what
    /// CHANGED: a screen transition, a life lost, the exit opening. The step
    /// being inspected gets the full picture whether anything changed or not.
    /// </remarks>
    private static void Report(int i, HostCall call, GameHost host, int? at)
    {
        var interesting = call.Kind != HostCallKind.Tick
                          || host.Screen != _lastScreen
                          || host.Session.Lives != _lastLives
                          || host.ExitOpen != _lastExit
                          || host.Collected != _lastCollected;

        if (interesting)
        {
            Console.WriteLine($"{i,7}  {Describe(call),-28}  {host.Screen,-9} "
                              + $"lives {host.Session.Lives}  "
                              + $"{host.Collected}/{host.Needed}"
                              + (host.ExitOpen ? "  exit open" : ""));
        }

        _lastScreen = host.Screen;
        _lastLives = host.Session.Lives;
        _lastExit = host.ExitOpen;
        _lastCollected = host.Collected;

        if (at is null || i != at - 1) return;

        Console.WriteLine();
        Console.WriteLine($"At call {i}: {Describe(call)}");
        Console.WriteLine($"  screen    {host.Screen}");
        Console.WriteLine($"  world     {host.World}  stage {host.Stage}");
        Console.WriteLine($"  frame     {host.Frame}   time {host.Time}");
        Console.WriteLine($"  score     {host.Session.Score}   lives {host.Session.Lives}");
        Console.WriteLine($"  treasure  {host.Collected} of {host.Needed}"
                          + (host.ExitOpen ? ", exit open" : ", exit shut"));
        Console.WriteLine($"  viewport  column {host.View.Column} row {host.View.Row}");

        if (host.Sim is { } sim)
        {
            // The cell, in the original's own addressing, so it can be read
            // against FORMATS.md rather than converted first.
            Console.WriteLine($"  player    cell 0x{sim.PlayerCell:X4}  pose {sim.PlayerPose}"
                              + (sim.PlayerDead ? "  DEAD" : ""));
            Console.WriteLine($"  random    seed 0x{sim.Random.Seed:X4}");
            Console.WriteLine($"  ticks     {sim.TickCounter}");
        }
        Console.WriteLine();
    }

    private static GameScreen _lastScreen = GameScreen.Title;
    private static int _lastLives = -1;
    private static bool _lastExit;
    private static int _lastCollected = -1;

    private static string Describe(HostCall call) => call.Kind switch
    {
        HostCallKind.Tick => $"tick {Action(call.Action)}",
        HostCallKind.MoveSelection => $"move selection {call.A:+0;-0}",
        HostCallKind.Load => $"load world {call.A} stage {call.B}",
        HostCallKind.Go => $"go {call.A:+0;-0}",
        HostCallKind.SetSelected => $"select world {call.A}",
        HostCallKind.SetScreen => $"screen := {(GameScreen)call.A}",
        _ => call.Kind.ToString(),
    };

    private static string Action(PlayerAction action)
    {
        var parts = new List<string>();
        if (action.Horizontal == PlayerAction.Left) parts.Add("left");
        if (action.Horizontal == PlayerAction.Right) parts.Add("right");
        if (action.Vertical == PlayerAction.Up) parts.Add("up");
        if (action.Vertical == PlayerAction.Down) parts.Add("down");
        if (action.Fire) parts.Add("fire");
        return parts.Count == 0 ? "idle" : string.Join('+', parts);
    }

    /// <summary>
    /// The same three files the browser reads, from a directory.
    /// </summary>
    /// <remarks>
    /// `tools/webassets.py` writes them, so the inspector and the front end are
    /// looking at the same data by construction rather than by a second reader
    /// being kept in step with the first.
    /// </remarks>
    private static bool TryLoadData(
        string gameDir, out byte[] cellmaps, out CaveTables tables, out WorldEntry[] worlds)
    {
        cellmaps = [];
        tables = new CaveTables();
        worlds = [];

        var maps = Path.Combine(gameDir, "cellmaps.bin");
        var caves = Path.Combine(gameDir, "caves.json");
        var worldList = Path.Combine(gameDir, "worlds.json");

        foreach (var needed in new[] { maps, caves, worldList })
        {
            if (File.Exists(needed)) continue;
            Console.Error.WriteLine($"No {Path.GetFileName(needed)} in {gameDir}. "
                                    + "Run tools/webassets.py first.");
            return false;
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        cellmaps = File.ReadAllBytes(maps);
        tables = JsonSerializer.Deserialize<CaveTables>(File.ReadAllText(caves), options)
                 ?? new CaveTables();
        worlds = JsonSerializer.Deserialize<WorldEntry[]>(File.ReadAllText(worldList), options)
                 ?? [];
        return true;
    }
}
