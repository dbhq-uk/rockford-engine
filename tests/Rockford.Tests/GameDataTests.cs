using Rockford.Engine;
using Rockford.Formats;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// Checks the reverse engineered formats against the real shipped files.
///
/// The game data is copyright First Star Software and is not distributable, so
/// these tests skip unless ROCKFORD_DATA points at an extracted game directory.
/// </summary>
public class GameDataTests
{
    private static CellmapsFile? Cellmaps()
    {
        var dir = GameFiles.DataDirectory;
        return dir is null ? null : CellmapsFile.Load(Path.Combine(dir, "CELLMAPS.BIN"));
    }

    [SkippableFact]
    public void Cellmaps_holds_forty_records()
    {
        var maps = Cellmaps();
        Skip.If(maps is null, "Set ROCKFORD_DATA to an extracted game directory.");

        Assert.Equal(40, maps!.Count);
    }

    [SkippableFact]
    public void Every_record_has_exactly_one_player_start_and_one_exit()
    {
        var maps = Cellmaps();
        Skip.If(maps is null, "Set ROCKFORD_DATA to an extracted game directory.");

        for (var r = 0; r < maps!.Count; r++)
        {
            var loaded = CaveLoader.Load(maps.Record(r));

            Assert.True(loaded.PlayerCell >= 0, $"record {r} has no player start");
            Assert.Equal(RuntimeTile.Player, loaded.Grid.ValueAt(loaded.PlayerCell));
            Assert.Equal(1, loaded.Grid.Count(RuntimeTile.ExitClosed));
        }
    }

    [SkippableFact]
    public void The_player_starts_on_authored_twenty_not_on_the_exit()
    {
        var maps = Cellmaps();
        Skip.If(maps is null, "Set ROCKFORD_DATA to an extracted game directory.");

        // The loader at 0x1093A gives authored 0x20 a position global; authored 0x74
        // becomes the quota-gated exit. An earlier engine had these the wrong way
        // round and started Rockford standing on the exit in every cave.
        for (var r = 0; r < maps!.Count; r++)
        {
            var authored = maps.Record(r);
            var loaded = CaveLoader.Load(authored);

            var at = CaveGrid.RowOf(loaded.PlayerCell) * CellmapsFile.Width
                     + CaveGrid.ColumnOf(loaded.PlayerCell);
            Assert.Equal(0x20, authored[at]);
        }
    }

    [SkippableFact]
    public void Every_record_is_walled_in_with_steel()
    {
        var maps = Cellmaps();
        Skip.If(maps is null, "Set ROCKFORD_DATA to an extracted game directory.");

        // The simulation relies on this. No handler does a bounds check; the border is
        // what stops a falling boulder walking off the edge of the grid.
        for (var r = 0; r < maps!.Count; r++)
        {
            var g = CaveLoader.Load(maps.Record(r)).Grid;

            for (var x = 0; x < CaveGrid.Width; x++)
            {
                Assert.Equal(RuntimeTile.Steel, g.ValueAt(x, 0));
                Assert.Equal(RuntimeTile.Steel, g.ValueAt(x, CaveGrid.Height - 1));
            }

            for (var y = 0; y < CaveGrid.Height; y++)
            {
                Assert.Equal(RuntimeTile.Steel, g.ValueAt(0, y));
                Assert.Equal(RuntimeTile.Steel, g.ValueAt(CaveGrid.Width - 1, y));
            }
        }
    }

    [SkippableFact]
    public void Every_authored_byte_is_one_the_loader_recognises()
    {
        var maps = Cellmaps();
        Skip.If(maps is null, "Set ROCKFORD_DATA to an extracted game directory.");

        // The loader turns anything it does not know into dirt. No shipped cave should
        // rely on that, so a failure here means a case is missing from the port.
        byte[] known =
        [
            0x00, 0x01, 0x02, 0x03, 0x04, 0x08, 0x0C, 0x10, 0x20, 0x28,
            0x2C, 0x2D, 0x2E, 0x30, 0x34, 0x35, 0x36, 0x37, 0x38,
            0x70, 0x74, 0x78, 0x7C, 0x84, 0x88, 0xC4,
        ];

        for (var r = 0; r < maps!.Count; r++)
        foreach (var b in maps.Record(r))
            Assert.Contains(b, known);
    }

    [SkippableFact]
    public void The_twenty_playable_records_are_not_the_first_twenty()
    {
        Skip.If(Cellmaps() is null, "Set ROCKFORD_DATA to an extracted game directory.");

        int[] expected = [0, 1, 2, 3, 8, 9, 10, 11, 16, 17, 18, 19, 24, 25, 26, 27, 32, 33, 34, 35];
        var actual = new List<int>();

        for (var menu = 0; menu < CellmapsFile.MenuWorlds.Length; menu++)
        for (var stage = 0; stage < CellmapsFile.CavesPerWorld; stage++)
            actual.Add(CellmapsFile.RecordOf(CellmapsFile.MenuWorlds[menu], stage));

        Assert.Equal(expected, actual);
    }

    [SkippableFact]
    public void No_cave_can_overflow_the_clock_field_on_the_status_row()
    {
        // The status row draws the clock in three characters, and its hundreds digit
        // is a single subtract rather than a loop - 0x122F8 tests 100 once and
        // 0x122FF subtracts it once. So that digit can only ever be a space or a
        // '1', and a clock of 200 or more would carry into the tens loop at 0x1230B
        // and print punctuation where a digit belongs: ':' at 200, ';' at 210.
        //
        // The original gets away with it because of the data, not the code. This is
        // the check that says so, and it is the reason the port does not clamp
        // something the original leaves unclamped.
        var maps = Cellmaps();
        var path = GameFiles.Executable;
        Skip.If(maps is null || path is null,
                "Set ROCKFORD_DATA to an extracted game directory and ROCKFORD_EXE to an unpacked executable.");

        var meta = CaveMetadata.Load(path!);
        var worst = 0;
        var worstCave = -1;
        var totalBonuses = 0;

        for (var cave = 0; cave < CaveMetadata.CaveCount; cave++)
        {
            // 0xFEAF adds one to the table value before it reaches the clock.
            var start = meta.TimeLimit(cave) + 1;

            // Every authored 0x7C is a time bonus worth twenty seconds, from
            // 0x100D2, and a cave can hold more than one - cave 31 holds two.
            var bonuses = 0;
            foreach (var b in maps!.Record(cave))
                if (b == 0x7C) bonuses++;

            totalBonuses += bonuses;

            if (start + 20 * bonuses > worst)
            {
                worst = start + 20 * bonuses;
                worstCave = cave;
            }
        }

        // The exact numbers, not just the range. Asserting only "under 200" is too
        // loose to notice this test's own arithmetic breaking: drop the plus one,
        // ignore the bonuses, or count the wrong authored byte and the answer is
        // still comfortably inside 100 to 199. All three of those were surviving
        // mutations until these three lines.
        Assert.Equal(9, totalBonuses);      // across all forty records
        Assert.Equal(34, worstCave);        // 170 in the table, plus one, plus one bonus
        Assert.Equal(191, worst);

        // Eight seconds of headroom, and the field breaks at 200.
        Assert.True(worst < 200, $"cave {worstCave} reaches {worst}, which the field cannot draw");
    }

    [SkippableFact]
    public void Every_playable_cave_runs_without_throwing()
    {
        var maps = Cellmaps();
        Skip.If(maps is null, "Set ROCKFORD_DATA to an extracted game directory.");

        for (var menu = 0; menu < CellmapsFile.MenuWorlds.Length; menu++)
        for (var stage = 0; stage < CellmapsFile.CavesPerWorld; stage++)
        {
            var loaded = CaveLoader.Load(maps!.PlayableRecord(menu, stage));
            var sim = new Simulation(loaded.Grid, 9999, randomSeed: 0x04D2)
            {
                PlayerCell = loaded.PlayerCell,
                CaveHasAmoeba = loaded.HasAmoeba,
                TreasureQuota = 1,
                // Enough that the clock cannot be what kills them. This asks whether
                // the cave itself is survivable, and a Simulation with no time set
                // now runs out after a second, which would answer a different
                // question entirely.
            };

            for (var i = 0; i < 200; i++)
            {
                sim.PlayerTick(PlayerAction.Idle);
                sim.Step();
            }
        }
    }

    [SkippableFact]
    public void Standing_still_is_survivable_in_most_caves()
    {
        var maps = Cellmaps();
        Skip.If(maps is null, "Set ROCKFORD_DATA to an extracted game directory.");

        var died = 0;

        for (var menu = 0; menu < CellmapsFile.MenuWorlds.Length; menu++)
        for (var stage = 0; stage < CellmapsFile.CavesPerWorld; stage++)
        {
            var loaded = CaveLoader.Load(maps!.PlayableRecord(menu, stage));
            var sim = new Simulation(loaded.Grid, 9999, randomSeed: 0x04D2)
            {
                PlayerCell = loaded.PlayerCell,
                CaveHasAmoeba = loaded.HasAmoeba,
                TreasureQuota = 1,
                // Enough that the clock cannot be what kills them. This asks whether
                // the cave itself is survivable, and a Simulation with no time set
                // now runs out after a second, which would answer a different
                // question entirely.
            };

            for (var i = 0; i < 60 && !sim.PlayerDead; i++)
            {
                sim.PlayerTick(PlayerAction.Idle);
                sim.Step();
            }

            if (sim.PlayerDead) died++;
        }

        // Some caves drop you next to something lethal on purpose. Most do not, and a
        // jump here means a rule has drifted rather than a cave being hard.
        Assert.InRange(died, 0, 12);
    }

    [SkippableFact]
    public void Ega_sheets_are_decodable()
    {
        var dir = GameFiles.DataDirectory;
        Skip.If(dir is null, "Set ROCKFORD_DATA to an extracted game directory.");

        var sheets = Directory.GetFiles(dir!, "*.FIL");
        Skip.If(sheets.Length == 0, "No EGA .FIL files here - this is probably the VGA release.");

        foreach (var f in sheets)
        {
            var img = EgaImage.Load(f);
            Assert.Equal(EgaImage.Width * EgaImage.Height, img.Pixels.Length);
            Assert.True(img.Pixels.ToArray().Distinct().Count() > 1, $"{f} decoded to a flat image.");
        }
    }
}
