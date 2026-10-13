using Rockford.Formats;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The per-cave numbers live in the executable, so these tests need an unpacked
/// image. Point ROCKFORD_EXE at one, or they skip.
/// </summary>
public class CaveMetadataTests
{
    private static CaveMetadata? Meta()
    {
        var path = GameFiles.Executable;
        return path is null ? null : CaveMetadata.Load(path);
    }

    [SkippableFact]
    public void Cave_one_needs_forty_treasure_in_a_hundred_and_ten_seconds()
    {
        var m = Meta();
        Skip.If(m is null, "Set ROCKFORD_EXE to an unpacked executable.");

        Assert.Equal(40, m!.RequiredTreasure(0));
        Assert.Equal(110, m.TimeLimit(0));
    }

    [SkippableFact]
    public void Treasure_is_worth_more_once_the_quota_is_met()
    {
        var m = Meta();
        Skip.If(m is null, "Set ROCKFORD_EXE to an unpacked executable.");

        // The original picks the table from the quota remaining BEFORE the
        // collection is counted, so the treasure that completes a quota still
        // scores at the lower rate.
        Assert.Equal(80, m!.TreasureScore(cave: 0, remainingQuota: 1));
        Assert.Equal(100, m.TreasureScore(cave: 0, remainingQuota: 0));
    }

    [SkippableFact]
    public void Every_cave_has_a_workable_clock_and_quota()
    {
        var m = Meta();
        Skip.If(m is null, "Set ROCKFORD_EXE to an unpacked executable.");

        for (var cave = 0; cave < CaveMetadata.CaveCount; cave++)
        {
            Assert.InRange(m!.TimeLimit(cave), 1, 3600);
            Assert.InRange(m.RequiredTreasure(cave), 1, 1000);
        }
    }

    [SkippableFact]
    public void The_last_cave_pays_more_before_its_quota_than_after()
    {
        var m = Meta();
        Skip.If(m is null, "Set ROCKFORD_EXE to an unpacked executable.");

        // Every cave but the last pays at least as much once the quota is met.
        for (var cave = 0; cave < CaveMetadata.CaveCount - 1; cave++)
            Assert.True(m!.TreasureScore(cave, 0) >= m.TreasureScore(cave, 1),
                $"cave {cave} pays less after the quota than before it");

        // Cave 40 inverts it, and by a long way. This is in the shipped tables, not
        // a misread: 5000 a treasure while the quota stands, 1000 once it is met.
        Assert.Equal(5000, m!.TreasureScore(cave: 39, remainingQuota: 1));
        Assert.Equal(1000, m.TreasureScore(cave: 39, remainingQuota: 0));
    }

    [SkippableFact]
    public void The_amoeba_cap_is_per_world_and_the_fourth_world_is_the_tight_one()
    {
        var m = Meta();
        Skip.If(m is null, "Set ROCKFORD_EXE to an unpacked executable.");

        // Ten worlds of four. Every world caps at 160 except the fourth, at 120.
        for (var cave = 0; cave < CaveMetadata.CaveCount; cave++)
        {
            var expected = CaveMetadata.WorldOf(cave) == 3 ? 120 : 160;
            Assert.Equal(expected, m!.AmoebaCap(cave));
        }
    }

    [SkippableFact]
    public void The_game_is_ten_worlds_of_four_not_five_of_eight()
    {
        var m = Meta();
        Skip.If(m is null, "Set ROCKFORD_EXE to an unpacked executable.");

        // Record number is 4 * world + stage, per the indexing at 0xFE9D.
        Assert.Equal(40, CaveMetadata.WorldCount * CaveMetadata.CavesPerWorld);
        Assert.Equal(0, CaveMetadata.WorldOf(3));
        Assert.Equal(1, CaveMetadata.WorldOf(4));
        Assert.Equal(9, CaveMetadata.WorldOf(39));
    }

    // ---- which release the image came from --------------------------------
    //
    // The tables used to be VGA offsets with no release attached, guarded only by
    // a length check. unpacked/ega.bin is LONGER than unpacked/vga.bin, so it
    // passed that check and the loader read the VGA addresses anyway - which in
    // the EGA binary are zeros. It returned an object saying every cave needs no
    // treasure, allows no time and pays nothing, and threw nothing.
    //
    // Both binaries are tracked here and ROCKFORD_EXE is documented in the
    // README, so pointing it at the EGA one was the obvious thing to try.

    [Fact]
    public void An_image_that_is_not_a_known_release_is_refused_rather_than_read()
    {
        // Long enough for every table of either layout, and not either release.
        // This is exactly the shape of input that used to produce zeros.
        var notRockford = new byte[256 * 1024];

        var thrown = Assert.Throws<ArgumentException>(
            () => CaveMetadata.FromImage(notRockford));

        Assert.Contains("UNPACKED", thrown.Message);
        Assert.Contains("VGA", thrown.Message);
        Assert.Contains("EGA", thrown.Message);
    }

    [Fact]
    public void A_short_image_is_refused_too()
    {
        Assert.Throws<ArgumentException>(() => CaveMetadata.FromImage(new byte[16]));
    }

    [SkippableFact]
    public void The_release_is_identified_from_the_instruction_that_reads_the_table()
    {
        var path = GameFiles.Executable;
        Skip.If(path is null, "Set ROCKFORD_EXE to an unpacked executable.");

        var m = CaveMetadata.Load(path!);

        // Whichever one is configured, it must have been recognised as one of
        // them rather than defaulted into.
        Assert.Contains(m.Release, CaveMetadata.Layouts);

        var image = File.ReadAllBytes(path!);
        Assert.True(image.AsSpan().IndexOf(m.Release.Signature.AsSpan()) >= 0,
                    $"{m.Release.Name} was chosen but its signature is not in the image");
    }

    // [SkippableFact], not [Fact]: under [Fact] the Skip.If below throws and fails the
    // test instead of skipping it, which only showed once the data was absent (#166).
    [SkippableFact]
    public void Both_releases_ship_the_same_forty_caves()
    {
        // The finding that made the fix worth making rather than just guarding:
        // the five tables are byte-identical across the two builds, so the EGA
        // release is the same forty caves at different addresses. If this ever
        // fails, the two releases have diverged and every quota, clock and score
        // on the site needs saying twice.
        var vga = Path(CaveMetadata.Vga);
        var ega = Path(CaveMetadata.Ega);
        Skip.If(vga is null || ega is null, "Both unpacked executables are needed.");

        var a = CaveMetadata.Load(vga!);
        var b = CaveMetadata.Load(ega!);

        Assert.Equal(CaveMetadata.Vga, a.Release);
        Assert.Equal(CaveMetadata.Ega, b.Release);

        for (var cave = 0; cave < CaveMetadata.CaveCount; cave++)
        {
            Assert.Equal(a.RequiredTreasure(cave), b.RequiredTreasure(cave));
            Assert.Equal(a.TimeLimit(cave), b.TimeLimit(cave));
            Assert.Equal(a.TreasureScore(cave, 1), b.TreasureScore(cave, 1));
            Assert.Equal(a.TreasureScore(cave, 0), b.TreasureScore(cave, 0));
            Assert.Equal(a.AmoebaCap(cave), b.AmoebaCap(cave));
        }
    }

    /// <summary>The tracked unpacked executable for a release, or null if absent.</summary>
    private static string? Path(CaveMetadata.Layout layout)
    {
        var name = layout == CaveMetadata.Vga ? "vga.bin" : "ega.bin";
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (!File.Exists(System.IO.Path.Combine(d.FullName, "Rockford.slnx"))) continue;
            var full = System.IO.Path.Combine(d.FullName, "unpacked", name);
            return File.Exists(full) ? full : null;
        }
        return null;
    }
}
