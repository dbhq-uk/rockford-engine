using System.Text.Json;
using Rockford.Formats;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The November 1987 release, r871109, against the 1988 one.
///
/// Two claims are load bearing for everything this repository now says about the
/// twenty caves the 1988 menu cannot reach, and both are checked here rather
/// than trusted.
///
/// The first is that 1987's cave file is 1988's first twenty records, unchanged.
/// That is what makes the alternating layout arithmetic rather than concealment:
/// five worlds, then five more appended, then a menu that picks every other one.
///
/// The second is that scuba and player have real artwork. They are not worlds
/// that were never drawn - they shipped, and were dropped a year later - so the
/// tilesets have to decode as completely as any other world's, at every position
/// the renderer can reach.
///
/// Both skip unless ROCKFORD_DATA and ROCKFORD_DATA_1987 are set, because the
/// game data is First Star's and is not distributable.
/// </summary>
public class NineteenEightySevenTests
{
    private const int CaveBytes = CellmapsFile.Width * CellmapsFile.Height;
    private const int CavesIn1987 = 20;

    [SkippableFact]
    public void The_1987_caves_are_the_first_twenty_records_of_the_1988_file()
    {
        var modern = GameFiles.DataDirectory;
        var early = GameFiles.DataDirectory1987;
        Skip.If(modern is null || early is null,
                "Set ROCKFORD_DATA and ROCKFORD_DATA_1987.");

        var oldCaves = File.ReadAllBytes(Path.Combine(early!, "CELLMAPS.BIN"));
        var newCaves = File.ReadAllBytes(Path.Combine(modern!, "CELLMAPS.BIN"));

        Assert.Equal(CavesIn1987 * CaveBytes, oldCaves.Length);
        Assert.Equal(40 * CaveBytes, newCaves.Length);

        // Byte for byte, not record by record: a record-wise comparison would pass
        // if the records were the same set in a different order, and the order is
        // the whole point.
        Assert.True(oldCaves.AsSpan().SequenceEqual(newCaves.AsSpan(0, oldCaves.Length)),
                    "the 1987 cave file is not the first twenty records of the 1988 one");
    }

    [SkippableTheory]
    [InlineData("SCUBA.FIL")]
    [InlineData("PLAYER.FIL")]
    public void A_withdrawn_world_is_as_complete_as_one_that_shipped(string sheet)
    {
        var early = GameFiles.DataDirectory1987;
        var root = GameFiles.Root;
        Skip.If(early is null || root is null, "Set ROCKFORD_DATA_1987.");

        // Hunter is the reference because it shipped in both releases and sits in
        // this same directory. Measuring against it rather than against the whole
        // origin list matters: a couple of origins are blank in EVERY sheet the
        // game ever had - (4,0) and (4,4), which main[] holds for values the
        // renderer never asks a sheet for - so "draws everywhere" is not a
        // property any world has, and a test asserting it fails on the originals.
        var reference = EgaImage.Load(Path.Combine(early!, "HUNTER.FIL"));
        var art = EgaImage.Load(Path.Combine(early!, sheet));

        var origins = SpriteOrigins(root!);
        Assert.NotEmpty(origins);

        var drawn = origins.Where(o => !Empty(reference, o.X, o.Y)).ToList();
        Assert.True(drawn.Count > 100,
                    $"the reference sheet only draws at {drawn.Count} positions");

        var blank = drawn
            .Where(o => Empty(art, o.X, o.Y))
            .Select(o => $"({o.X},{o.Y})")
            .ToList();

        Assert.True(blank.Count == 0,
                    $"{sheet} draws nothing at {blank.Count} of {drawn.Count} "
                    + $"positions a shipped world draws: {string.Join(", ", blank.Take(8))}");
    }

    /// <summary>
    /// Every position the front end can ask a sheet for, from the tables the
    /// startup code converts - see tools/webassets.py. Read from the committed
    /// sprites.json rather than restated here, so the two cannot drift.
    /// </summary>
    internal static List<(int X, int Y)> SpriteOrigins(string root)
    {
        var path = Path.Combine(root, "web", "Rockford.Web", "wwwroot", "game", "sprites.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var origins = new HashSet<(int, int)>();
        foreach (var table in document.RootElement.EnumerateObject())
        {
            if (table.Name == "never") continue;   // values the renderer skips
            foreach (var entry in table.Value.EnumerateArray())
            {
                var x = entry[0].GetInt32();
                var y = entry[1].GetInt32();
                if (x >= 0 && y >= 0) origins.Add((x, y));
            }
        }
        return origins.ToList();
    }

    /// <summary>
    /// A 16 by 16 tile with nothing in it. Colour 0 is the sheet's own backdrop,
    /// which the front end punches out, so a tile of nothing but 0 is a tile that
    /// draws nothing.
    /// </summary>
    internal static bool Empty(EgaImage art, int x, int y)
    {
        for (var row = 0; row < 16; row++)
            for (var column = 0; column < 16; column++)
                if (art[x + column, y + row] != 0)
                    return false;
        return true;
    }
}
