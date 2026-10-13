using Rockford.Formats;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// Aleksi Sahala's five hidden-world sheets, which the faithful build draws
/// (third_party/rockford-patcher, tools/webassets.py SAHALA).
///
/// His patcher loads each one in place of a shipped world's sheet, so a position
/// he left empty is a hole in the cave, not a missing extra. These hold every one
/// of them to the positions a 1988 sheet draws. Both files are in the private
/// repository and not the public one (#166), so it skips where they are absent.
/// </summary>
public class SahalaArtTests
{
    [SkippableTheory]
    [InlineData("HUNTER.EGA", "scuba")]
    [InlineData("COOK.EGA", "player")]
    [InlineData("COWBOY.EGA", "music")]
    [InlineData("SPACE.EGA", "luck")]
    [InlineData("BODY.EGA", "miner")]
    public void His_sheet_draws_everywhere_a_shipped_world_does(string sheet, string world)
    {
        var root = GameFiles.Root ?? throw new InvalidOperationException("no Rockford.slnx above the test binary");

        var referencePath = Path.Combine(root, "extracted", "ega1", "HUNTER.FIL");
        var artPath = Path.Combine(root, "third_party", "rockford-patcher", sheet);
        Skip.IfNot(File.Exists(referencePath) && File.Exists(artPath),
                   "The 1988 sheet and Sahala's art are not in this checkout.");

        var reference = EgaImage.Load(referencePath);
        var art = EgaImage.Load(artPath);

        var drawn = NineteenEightySevenTests.SpriteOrigins(root)
            .Where(o => !NineteenEightySevenTests.Empty(reference, o.X, o.Y))
            .ToList();
        Assert.True(drawn.Count > 100, $"the reference sheet only draws at {drawn.Count} positions");

        var blank = drawn
            .Where(o => NineteenEightySevenTests.Empty(art, o.X, o.Y))
            .Select(o => $"({o.X},{o.Y})")
            .ToList();

        Assert.True(blank.Count == 0,
                    $"{world} ({sheet}) draws nothing at {blank.Count} of {drawn.Count} "
                    + $"positions a shipped world draws: {string.Join(", ", blank.Take(8))}");
    }
}
