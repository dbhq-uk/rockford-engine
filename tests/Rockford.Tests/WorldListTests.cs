using System.Text.Json;
using Rockford.Engine;
using Rockford.Web;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// Which worlds each build offers, and the manifest that says so (#44).
/// </summary>
public class WorldListTests
{
    private static readonly string[] MenuNames =
        ["hunter", "cook", "cowboy", "space", "body", "scuba", "player", "music", "luck", "miner"];

    private static readonly int[] MenuRaw = [0, 2, 4, 6, 8, 1, 3, 5, 7, 9];

    /// <summary>
    /// Ten worlds with a part-drawn second page, as the manifest described them on
    /// 28 September 2026: the first page drawable by both builds, scuba and player
    /// by the faithful build only, the last three by neither. The committed
    /// manifest has moved on (see the tests below it); this is the shape the
    /// paging rules have to handle, so it stays.
    /// </summary>
    private static WorldEntry[] Today() =>
    [
        .. MenuNames.Select((name, i) => new WorldEntry
        {
            Sheet = name,
            World = MenuRaw[i],
            Original = i < 7,
            Vip = i < 5,
        }),
    ];

    [Fact]
    public void Whole_pages_only_keeps_a_part_drawn_page_off_the_screen()
    {
        // The 18 September rule: nothing on the second page until every world on
        // it has art. Scuba and player have theirs; music, luck and miner do not,
        // so the page stays shut in both builds.
        Assert.Equal(MenuNames[..5], WorldList.Offered(Today(), vip: false, wholePagesOnly: true).Select(w => w.Sheet));
        Assert.Equal(MenuNames[..5], WorldList.Offered(Today(), vip: true, wholePagesOnly: true).Select(w => w.Sheet));
    }

    [Fact]
    public void World_by_world_offers_whatever_the_build_can_draw_in_menu_order()
    {
        Assert.Equal(MenuNames[..7], WorldList.Offered(Today(), vip: false, wholePagesOnly: false).Select(w => w.Sheet));

        // VIP has no atlases for scuba or player yet (#45), so it offers neither.
        Assert.Equal(MenuNames[..5], WorldList.Offered(Today(), vip: true, wholePagesOnly: false).Select(w => w.Sheet));
    }

    [Fact]
    public void A_manifest_from_before_the_flags_reads_as_it_always_did()
    {
        // Five entries with no availability fields, which is every worlds.json
        // written before this change. They have to come back as five drawable
        // worlds, not as five worlds nobody can draw.
        const string old = """
            [{"sheet":"hunter","world":0,"floor":"#453555"},
             {"sheet":"cook","world":2,"floor":"#555555"},
             {"sheet":"cowboy","world":4,"floor":"#355565"},
             {"sheet":"space","world":6,"floor":"#004575"},
             {"sheet":"body","world":8,"floor":"#650000"}]
            """;
        var worlds = JsonSerializer.Deserialize<WorldEntry[]>(old, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(5, WorldList.Offered(worlds, vip: false, wholePagesOnly: true).Length);
        Assert.Equal(5, WorldList.Offered(worlds, vip: true, wholePagesOnly: true).Length);
    }

    [Fact]
    public void Pages_are_five_boxes()
    {
        Assert.Equal((0, 4), (WorldList.PageOf(4), WorldList.BoxOf(4)));
        Assert.Equal((1, 0), (WorldList.PageOf(5), WorldList.BoxOf(5)));
        Assert.Equal((1, 4), (WorldList.PageOf(9), WorldList.BoxOf(9)));
    }

    [Fact]
    public void A_replay_is_built_over_the_list_the_recording_was_made_on()
    {
        // The inspector reads worlds.json, which is all ten. The browser recorded
        // against the few its build could draw. Built over the ten, the replay is
        // refused on a world-list mismatch nobody asked for.
        var manifest = Today();
        var faithful = WorldList.Offered(manifest, vip: false, wholePagesOnly: false);
        var vip = WorldList.Offered(manifest, vip: true, wholePagesOnly: false);
        Assert.NotEqual(faithful.Length, vip.Length);

        // Under the standing rule the two builds happen to agree; the choice is
        // made on the hash either way, so it holds whichever rule is in force.
        foreach (var recorded in new[] { WorldList.Offered(manifest, false), WorldList.Offered(manifest, true) })
        {
            var chosen = WorldList.AsRecorded(manifest, SessionIdentity.WorldsHashOf(recorded));
            Assert.Equal(SessionIdentity.WorldsHashOf(recorded), SessionIdentity.WorldsHashOf(chosen));
        }

        // And a list neither build would offer comes back as the faithful one,
        // so the replay's own check can refuse it with the real hashes.
        var stranger = WorldList.AsRecorded(manifest, "not a hash");
        Assert.Equal(SessionIdentity.WorldsHashOf(WorldList.Offered(manifest, false)),
                     SessionIdentity.WorldsHashOf(stranger));
    }

    // ---- the manifest as committed ------------------------------------------

    /// <summary>
    /// The art and game data are not in the public repository (#166): they are added
    /// from the private one when the site is assembled. The manifest is, so the tests
    /// of what it says always run; the tests that the files it names exist run where
    /// the files are.
    /// </summary>
    private static void SkipWithoutArt() =>
        Skip.IfNot(File.Exists(Path.Combine(GameDirectory(), "hunter.png")),
                   "The game's art is not in this checkout.");

    private static string GameDirectory()
    {
        var root = GameFiles.Root ?? throw new InvalidOperationException("no Rockford.slnx above the test binary");
        return Path.Combine(root, "web", "Rockford.Web", "wwwroot", "game");
    }

    private static WorldEntry[] Committed() =>
        JsonSerializer.Deserialize<WorldEntry[]>(
            File.ReadAllText(Path.Combine(GameDirectory(), "worlds.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void The_committed_manifest_is_all_ten_in_the_executables_menu_order()
    {
        // Read out of ds:0x26F2 and ds:0x267A by tools/webassets.py rather than
        // typed, so this pins what that produced against what the executable says.
        var worlds = Committed();

        Assert.Equal(MenuNames, worlds.Select(w => w.Sheet));
        Assert.Equal(MenuRaw, worlds.Select(w => w.World));
    }

    [SkippableFact]
    public void The_five_shipped_worlds_have_a_cartoon_strip_and_the_hidden_five_have_none()
    {
        SkipWithoutArt();
        // A strip is 48 by 48 frames, six across and three down - WIN, LOSE and
        // TIME (tools/cartoons.py). The hidden worlds are drawn from Aleksi
        // Sahala's art, which has no cartoons, so they play none (#137).
        var worlds = Committed().ToDictionary(w => w.Sheet);

        foreach (var shipped in MenuNames[..5])
        {
            Assert.Equal($"{shipped}-cartoon.png", worlds[shipped].Cartoon);
            Assert.True(File.Exists(Path.Combine(GameDirectory(), worlds[shipped].Cartoon!)), shipped);
        }

        foreach (var hidden in MenuNames[5..])
            Assert.Null(worlds[hidden].Cartoon);
    }

    [SkippableFact]
    public void VIP_has_its_own_redrawn_strip_for_each_of_the_five_cartoons()
    {
        SkipWithoutArt();
        // The same sheet at eight to one: six frames of 384 across, three rows down
        // (tools/vipcartoons.py). VIP plays these and none of the original's.
        var worlds = Committed().ToDictionary(w => w.Sheet);

        foreach (var shipped in MenuNames[..5])
        {
            var file = Cartoons.StripFor(worlds[shipped].Cartoon!, vip: true);
            Assert.Equal($"{shipped}-cartoon-x8.png", file);
            Assert.True(File.Exists(Path.Combine(GameDirectory(), file)), file);
        }
    }

    [Fact]
    public void The_committed_manifest_says_what_each_build_can_draw()
    {
        var worlds = Committed().ToDictionary(w => w.Sheet);

        foreach (var shipped in MenuNames[..5])
            Assert.True(worlds[shipped].Original && worlds[shipped].Vip, shipped);

        // The five the 1988 menu cannot reach, all in Aleksi Sahala's art
        // (third_party/rockford-patcher) - scuba and player over their 1987
        // originals, so the page is one set. Both builds since 3 October 2026,
        // when VIP redrew them (#110).
        foreach (var hidden in MenuNames[5..])
        {
            Assert.True(worlds[hidden].Original, hidden);
            Assert.True(worlds[hidden].Vip, hidden);
            Assert.Equal($"portraits/{hidden}.png", worlds[hidden].Portrait);
        }
    }

    [Fact]
    public void Under_the_standing_rule_both_builds_offer_all_ten()
    {
        // Dan, 28 September 2026: each world as soon as its build can draw it. The
        // faithful build's second page held all five hidden worlds from 29
        // September; VIP's joined it on 3 October, once it had atlases and a baked
        // second page for them (#45, #110, #111).
        var worlds = Committed();

        Assert.Equal(MenuNames, WorldList.Offered(worlds, vip: false).Select(w => w.Sheet));
        Assert.Equal(MenuNames, WorldList.Offered(worlds, vip: true).Select(w => w.Sheet));
    }

    [Fact]
    public void A_recording_made_before_the_second_page_still_replays()
    {
        // Every faithful recording made before 28 September pinned five worlds,
        // because that is all either build offered. Against the committed manifest
        // it replays whichever way the builds' lists have moved, because the 1988
        // five are a candidate of their own.
        var hash = SessionIdentity.WorldsHashOf(Before());

        var chosen = WorldList.AsRecorded(Committed(), hash);

        Assert.Equal(hash, SessionIdentity.WorldsHashOf(chosen));
    }

    [Fact]
    public void A_recording_made_on_the_five_replays_once_both_builds_offer_all_ten()
    {
        // The case #109 exists for: VIP has its hidden worlds, so neither build's
        // list is the old five. Before the fix this fell through to the faithful
        // list and the replay was refused on a world-list mismatch.
        var both = MenuNames.Select((name, i) => new WorldEntry
        {
            Sheet = name, World = MenuRaw[i], Original = true, Vip = true,
        }).ToArray();
        Assert.Equal(10, WorldList.Offered(both, vip: false).Length);
        Assert.Equal(10, WorldList.Offered(both, vip: true).Length);

        var hash = SessionIdentity.WorldsHashOf(Before());
        var chosen = WorldList.AsRecorded(both, hash);

        Assert.Equal(hash, SessionIdentity.WorldsHashOf(chosen));
        Assert.Equal(5, chosen.Length);
    }

    [Fact]
    public void Each_builds_own_list_is_still_chosen_when_it_is_the_one_recorded()
    {
        // The two builds differ here, as the committed manifest has them today, so
        // each list is told apart from the other and from the 1988 five.
        var manifest = Today();
        var faithful = WorldList.Offered(manifest, vip: false);
        var vip = WorldList.Offered(manifest, vip: true);

        Assert.Equal(SessionIdentity.WorldsHashOf(faithful),
            SessionIdentity.WorldsHashOf(WorldList.AsRecorded(manifest, SessionIdentity.WorldsHashOf(faithful))));
        Assert.Equal(SessionIdentity.WorldsHashOf(vip),
            SessionIdentity.WorldsHashOf(WorldList.AsRecorded(manifest, SessionIdentity.WorldsHashOf(vip))));
    }

    [Fact]
    public void A_hash_nothing_offered_still_falls_back_to_the_faithful_list()
    {
        // Including when the 1988 five are among the candidates: a stranger is
        // still handed the faithful list, so the replay's own check can refuse it
        // with real hashes.
        var both = MenuNames.Select((name, i) => new WorldEntry
        {
            Sheet = name, World = MenuRaw[i], Original = true, Vip = true,
        }).ToArray();

        var stranger = WorldList.AsRecorded(both, "not a hash");

        Assert.Equal(SessionIdentity.WorldsHashOf(WorldList.Offered(both, vip: false)),
                     SessionIdentity.WorldsHashOf(stranger));
    }

    private static WorldEntry[] Before() =>
        [.. MenuNames[..5].Select((name, i) => new WorldEntry { Sheet = name, World = MenuRaw[i] })];

    // ---- the select screen's pictures (#111) -----------------------------------

    [Theory]
    [InlineData(false, 0, "game/menu_i.png")]
    [InlineData(false, 1, "game/menu_i.png")]
    [InlineData(true, 0, "vip/menu.png")]
    [InlineData(true, 1, "vip/menu-2.png")]
    [InlineData(true, 2, "vip/menu-3.png")]
    public void Each_build_picks_its_own_select_picture_for_a_page(bool vip, int page, string expected)
    {
        Assert.Equal(expected, WorldList.MenuPicture(vip, page));
    }

    [Fact]
    public void Only_the_faithful_build_paints_portraits_over_its_picture()
    {
        // VIP bakes every page's portraits into the page's own picture. Painting
        // the faithful build's 48 pixel portraits over VIP's large, non-square
        // boxes was what #111 found would stretch them.
        Assert.True(WorldList.PortraitsAreBaked(false, 0));
        Assert.False(WorldList.PortraitsAreBaked(false, 1));
        Assert.True(WorldList.PortraitsAreBaked(true, 0));
        Assert.True(WorldList.PortraitsAreBaked(true, 1));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(10, 2)]
    [InlineData(11, 3)]
    public void Worlds_fill_pages_of_five(int worlds, int pages)
    {
        Assert.Equal(pages, WorldList.PageCount(worlds));
    }

    [SkippableFact]
    public void VIP_offers_a_page_only_if_its_baked_picture_is_in_the_build()
    {
        SkipWithoutArt();
        // The guard that makes it impossible to flip a hidden world's `vip` flag
        // before the second page's art exists: a world on page N+1 with no
        // vip/menu-(N+2).png would leave the select screen with nothing to draw.
        var dir = Path.Combine(GameDirectory(), "..", "vip");
        var offered = WorldList.Offered(Committed(), vip: true);

        for (var page = 0; page < WorldList.PageCount(offered.Length); page++)
        {
            var picture = WorldList.MenuPicture(vip: true, page);
            var file = Path.Combine(dir, Path.GetFileName(picture));
            Assert.True(File.Exists(file), $"VIP offers page {page + 1} but {picture} does not exist");
        }
    }

    [SkippableFact]
    public void Every_world_the_faithful_build_offers_has_its_sheet_and_portrait()
    {
        SkipWithoutArt();
        var dir = GameDirectory();
        foreach (var world in Committed().Where(w => w.Original))
        {
            Assert.True(File.Exists(Path.Combine(dir, $"{world.Sheet}.png")), $"{world.Sheet}.png");
            if (world.Portrait is { } portrait)
                Assert.True(File.Exists(Path.Combine(dir, portrait)), portrait);
        }
    }
}
