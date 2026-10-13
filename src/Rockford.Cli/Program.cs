using Rockford.Engine;
using Rockford.Formats;

namespace Rockford.Cli;

/// <summary>
/// Runs the caves in a terminal.
///
/// This is the diagnostic front end. It exists for watching the simulation and
/// checking a rule change, not for playing: the browser front end under
/// <c>web/</c> draws the game's own sprites.
///
/// The game data is not shipped with this repository. Point ROCKFORD_DATA at a
/// directory holding CELLMAPS.BIN, or pass the path as the first argument.
/// </summary>
public static class Program
{
    private const int Playable = 20;

    private static readonly string[] Worlds = ["hunter", "cook", "cowboy", "space", "body"];

    public static int Main(string[] args)
    {
        // Before the game-data check, because a replay reads the browser's own
        // JSON rather than a disk image and needs no CELLMAPS.BIN beside it.
        if (ArgValue(args, "--replay") is { } recording)
        {
            var gameDir = ArgValue(args, "--game-dir")
                          ?? Path.Combine("web", "Rockford.Web", "wwwroot", "game");
            var at = ArgValue(args, "--at") is { } stopAt ? int.Parse(stopAt) : (int?)null;
            return Inspector.Run(recording, gameDir, at);
        }

        // Also before the game-data check: this one reads the disk the game
        // shipped on, not the extracted data the rest of the CLI wants.
        if (args.Contains("--scan"))
        {
            var vol = (ArgValue(args, "--volume") ?? Path.Combine("extracted", "vga")).Split(',');
            return Sweep.Scan(vol,
                ArgValue(args, "--caves") is { } n1 ? int.Parse(n1) : 40,
                ArgValue(args, "--steps") is { } n2 ? int.Parse(n2) : 20);
        }

        if (args.Contains("--sweep"))
        {
            var vol = (ArgValue(args, "--volume") ?? Path.Combine("extracted", "vga")).Split(',');
            return Sweep.Run(vol, ArgValue(args, "--caves") is { } howMany ? int.Parse(howMany) : 40);
        }

        if (args.Contains("--original"))
        {
            // Named, not positional. The rest of this CLI takes a bare
            // directory, but --limit takes a number and a bare number is
            // indistinguishable from a path, which cost a confusing
            // "No directory at 20000000".
            var volume = (ArgValue(args, "--volume")
                          ?? string.Join(',', Path.Combine("extracted", "ega1"),
                                              Path.Combine("extracted", "ega2")))
                .Split(',');
            var budget = ArgValue(args, "--limit") is { } l ? long.Parse(l) : 50_000_000L;
            var stop = ArgValue(args, "--stop-at") is { } s
                ? Convert.ToInt32(s, 16) : (int?)null;
            return Original.Run(volume, budget, stop, ArgValue(args, "--keys") ?? "N", ArgValue(args, "--dump"),
                ArgValue(args, "--press"));
        }

        var dir = args.FirstOrDefault(a => !a.StartsWith('-'))
                  ?? Environment.GetEnvironmentVariable("ROCKFORD_DATA");

        if (dir is null)
        {
            Console.Error.WriteLine("""
                Usage: rockford <game-data-dir> [--cave N] [--list] [--soak] [--demo]
                       rockford --replay <recording.json> [--game-dir DIR] [--at N]

                The directory must hold CELLMAPS.BIN from an original Rockford disk.
                Set ROCKFORD_DATA instead of passing it, if you prefer, and
                ROCKFORD_EXE at an unpacked executable for the real quotas.

                  --list    every playable cave and what is in it
                  --soak    run them all with no input and report what happens
                  --demo    watch one cave settle, no input
                  --cave N  which cave, 1 to 20

                  --replay F  walk a recorded session and say what happened
                  --at N      stop there and report the state in full
                  --game-dir  where cellmaps.bin, caves.json and worlds.json are
                              (default web/Rockford.Web/wwwroot/game)
                """);
            return 2;
        }

        var path = Path.Combine(dir, "CELLMAPS.BIN");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No CELLMAPS.BIN in {dir}.");
            return 2;
        }

        var maps = CellmapsFile.Load(path);
        var meta = Metadata();

        // Caves are numbered 1 to 20 here, which is what the game offers: five worlds
        // of four. The records behind them are not contiguous - see CellmapsFile.
        var cave = ArgValue(args, "--cave") is { } n ? int.Parse(n) - 1 : 0;
        cave = Math.Clamp(cave, 0, Playable - 1);

        if (args.Contains("--list")) return List(maps, meta);
        if (args.Contains("--soak")) return Soak(maps, meta);
        if (args.Contains("--demo")) return Demo(maps, meta, cave);
        return Play(maps, meta, cave);
    }

    /// <summary>
    /// The per-cave tables live in the executable, so they are optional here. Without
    /// one, the quota falls back to the treasure actually in the cave, which is a
    /// guess and is marked as one in the listing.
    /// </summary>
    private static CaveMetadata? Metadata()
    {
        var exe = Environment.GetEnvironmentVariable("ROCKFORD_EXE");
        return exe is not null && File.Exists(exe) ? CaveMetadata.Load(exe) : null;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int RecordOf(int cave) =>
        CellmapsFile.RecordOf(CellmapsFile.MenuWorlds[cave / CellmapsFile.CavesPerWorld],
                              cave % CellmapsFile.CavesPerWorld);

    private static Simulation Open(CellmapsFile maps, CaveMetadata? meta, int cave)
    {
        var record = RecordOf(cave);
        var loaded = CaveLoader.Load(maps.Record(record));

        var quota = meta?.RequiredTreasure(record)
                    ?? Math.Max(1, loaded.Grid.Count(RuntimeTile.Treasure) * 3 / 4);

        return new Simulation(loaded.Grid, meta?.TimeLimit(record) ?? 120,
                              randomSeed: 0x04D2)
        {
            PlayerCell = loaded.PlayerCell,
            CaveHasAmoeba = loaded.HasAmoeba,
            AmoebaCap = meta?.AmoebaCap(record) ?? 160,
            TreasureQuota = quota,
            ScoreBeforeQuota = meta?.TreasureScore(record, 1) ?? 10,
            ScoreAfterQuota = meta?.TreasureScore(record, 0) ?? 10,
        };
    }

    private static int List(CellmapsFile maps, CaveMetadata? meta)
    {
        if (meta is null)
            Console.WriteLine("No ROCKFORD_EXE, so the quotas below are guessed.\n");

        Console.WriteLine("cave  world    record  treasure  boulders  creatures  quota  time");

        for (var c = 0; c < Playable; c++)
        {
            var record = RecordOf(c);
            var g = CaveLoader.Load(maps.Record(record)).Grid;
            var sim = Open(maps, meta, c);

            var creatures = 0;
            foreach (var a in g.ScanOrder())
                if (RuntimeTile.IsEnemy(g.ValueAt(a)))
                    creatures++;

            Console.WriteLine(
                $"{c + 1,4}  {Worlds[c / CellmapsFile.CavesPerWorld],-7}  {record,6}  "
                + $"{g.Count(RuntimeTile.Treasure),8}  {g.Count(RuntimeTile.Boulder),8}  "
                + $"{creatures,9}  {sim.TreasureQuota,5}  {sim.TimeRemaining,4}");
        }

        return 0;
    }

    /// <summary>
    /// Runs every cave with no input. A quick way to see whether a rule change has
    /// made the caves behave unreasonably.
    /// </summary>
    private static int Soak(CellmapsFile maps, CaveMetadata? meta)
    {
        Console.WriteLine("cave  world    steps  outcome");

        for (var c = 0; c < Playable; c++)
        {
            var sim = Open(maps, meta, c);

            var step = 0;
            for (; step < 100 && !sim.PlayerDead && !sim.CaveComplete; step++)
            {
                sim.PlayerTick(PlayerAction.Idle);
                sim.Step();
            }

            var outcome = sim.PlayerDead ? "caught" : sim.CaveComplete ? "out" : "still going";
            Console.WriteLine(
                $"{c + 1,4}  {Worlds[c / CellmapsFile.CavesPerWorld],-7}  {step,5}  {outcome}");
        }

        return 0;
    }

    private static int Demo(CellmapsFile maps, CaveMetadata? meta, int cave)
    {
        var sim = Open(maps, meta, cave);

        for (var i = 0; i < 60 && !sim.PlayerDead; i++)
        {
            sim.PlayerTick(PlayerAction.Idle);
            sim.Step();
            Draw(sim, cave);
            Thread.Sleep(100);
        }

        return 0;
    }

    private static int Play(CellmapsFile maps, CaveMetadata? meta, int cave)
    {
        var sim = Open(maps, meta, cave);
        Console.CursorVisible = false;

        try
        {
            Draw(sim, cave);

            while (!sim.PlayerDead && !sim.CaveComplete)
            {
                var action = ReadAction(out var quit);
                if (quit) break;

                sim.PlayerTick(action);
                sim.Step();
                Draw(sim, cave);
            }

            Console.WriteLine(sim.CaveComplete ? $"\nOut. Score {sim.Score}."
                            : sim.PlayerDead ? "\nCaught."
                            : "\nQuit.");
        }
        finally
        {
            Console.CursorVisible = true;
        }

        return 0;
    }

    private static PlayerAction ReadAction(out bool quit)
    {
        quit = false;
        var key = Console.ReadKey(intercept: true);

        // Holding shift turns a direction into a shot, which is how the original
        // separated moving from firing on a keyboard.
        var fire = (key.Modifiers & ConsoleModifiers.Shift) != 0;

        switch (key.Key)
        {
            case ConsoleKey.LeftArrow or ConsoleKey.A:
                return new PlayerAction(PlayerAction.Left, 0, fire);
            case ConsoleKey.RightArrow or ConsoleKey.D:
                return new PlayerAction(PlayerAction.Right, 0, fire);
            case ConsoleKey.UpArrow or ConsoleKey.W:
                return new PlayerAction(0, PlayerAction.Up, fire);
            case ConsoleKey.DownArrow or ConsoleKey.S:
                return new PlayerAction(0, PlayerAction.Down, fire);
            case ConsoleKey.Q or ConsoleKey.Escape:
                quit = true;
                return PlayerAction.Idle;
            default:
                return PlayerAction.Idle;
        }
    }

    private static void Draw(Simulation sim, int cave)
    {
        Console.SetCursorPosition(0, 0);
        var sb = new System.Text.StringBuilder();

        sb.Append($"cave {cave + 1}  {Worlds[cave / CellmapsFile.CavesPerWorld]}   ")
          .Append($"treasure {sim.TreasureQuota} to go   ")
          .Append($"score {sim.Score}   ")
          .Append(sim.TreasureQuota == 0 ? "EXIT OPEN " : "          ")
          .Append("\n\n");

        for (var y = 0; y < CaveGrid.Height; y++)
        {
            for (var x = 0; x < CaveGrid.Width; x++)
                sb.Append(Glyph(sim.Grid.ValueAt(x, y)));
            sb.Append('\n');
        }

        sb.Append("\narrows move, shift+arrow fires, q quits\n");
        Console.Write(sb.ToString());
    }

    private const string Reset = "[0m";

    private static string Ink(string colour, string glyph) => $"[{colour}m{glyph}{Reset}";

    private static string Glyph(byte v) => v switch
    {
        RuntimeTile.Space => "  ",
        RuntimeTile.Dirt => Ink("33", "::"),
        RuntimeTile.Steel => Ink("37;1", "##"),
        RuntimeTile.Wall => Ink("31", "=="),
        RuntimeTile.PassThroughWall => Ink("35", "<>"),
        RuntimeTile.ExpandingWall or RuntimeTile.ExpandingWallDeferred => Ink("35;1", "<>"),
        RuntimeTile.Player => Ink("92;1", "@@"),
        RuntimeTile.ExitClosed => Ink("31", "EE"),
        RuntimeTile.EnemyClockwise or RuntimeTile.EnemyClockwiseMoved => Ink("93;1", "WW"),
        RuntimeTile.EnemyAnticlockwise or RuntimeTile.EnemyAnticlockwiseMoved => Ink("91;1", "MM"),
        >= RuntimeTile.Treasure and <= RuntimeTile.TreasureFallingDeferred => Ink("96;1", "<>"),
        >= RuntimeTile.Boulder and <= RuntimeTile.BoulderFallingDeferred => Ink("37", "()"),
        >= RuntimeTile.Amoeba and <= RuntimeTile.AmoebaLast => Ink("32;1", "%%"),
        >= RuntimeTile.TimedWall and <= RuntimeTile.TimedWallLast => Ink("95", "~~"),
        >= RuntimeTile.ExitOpen and <= RuntimeTile.ExitOpenLast => Ink("92;5", "EE"),
        >= 0x11 and <= 0x14 => Ink("93;1", "**"),
        >= 0x1B and <= 0x1E => Ink("91;1", "**"),
        _ => Ink("90", ".."),
    };
}
