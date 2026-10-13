namespace Rockford.Engine;

/// <summary>
/// One of the ten worlds, as <c>worlds.json</c> holds it, in menu order.
///
/// This lived in the browser component until the host state machine was pulled out
/// of it. It is data rather than presentation - the world number is what indexes the
/// cave records - so it belongs beside the thing that reads it.
/// </summary>
public sealed class WorldEntry
{
    public string Sheet { get; set; } = "";

    /// <summary>The raw world number, which is what indexes the cave records.</summary>
    public int World { get; set; }

    /// <summary>The cave floor colour, palette index 0 of this world's sheet.</summary>
    public string Floor { get; set; } = "#000";

    /// <summary>
    /// Whether the faithful build has a sheet to draw this world with. True for
    /// all ten: the five 1988 worlds, and the five hidden ones in Aleksi Sahala's
    /// reconstructions (third_party/rockford-patcher).
    /// </summary>
    /// <remarks>
    /// Defaults to true so that a manifest written before these fields existed
    /// reads as it always did: five worlds, all drawable.
    /// </remarks>
    public bool Original { get; set; } = true;

    /// <summary>Whether VIP has redrawn atlases for this world.</summary>
    public bool Vip { get; set; } = true;

    /// <summary>
    /// A 48 by 48 portrait to draw into this world's box on the select screen, or
    /// null when the select picture already carries one - which every 1988 world's
    /// does, because MENU_I was drawn with them in it.
    /// </summary>
    public string? Portrait { get; set; }

    /// <summary>
    /// The sheet of this world's three cartoons - WIN, LOSE and TIME, six 48 by 48
    /// frames each - or null when it has none. Only the five 1988 worlds do: the
    /// hidden five are drawn from art that carries no cartoons (#137).
    /// </summary>
    public string? Cartoon { get; set; }
}

/// <summary>
/// Which worlds a build offers, and how they fall onto pages of the select screen.
/// </summary>
/// <remarks>
/// <para>
/// The select picture has five boxes, so the ten worlds are two pages of five in
/// the executable's own menu order - <c>0, 2, 4, 6, 8</c> then <c>1, 3, 5, 7, 9</c>,
/// the table at <c>ds:0x26F2</c>. The first page is what 1988 shipped; the second
/// is the five its menu cannot reach (#44).
/// </para>
/// <para>
/// A build only offers what it can draw. Both builds read one manifest and they
/// cannot draw the same worlds, so the choice is made here from the manifest's own
/// per-build flags, not by trying to load a sheet and seeing what fails.
/// </para>
/// </remarks>
public static class WorldList
{
    /// <summary>Boxes on the select picture.</summary>
    public const int PageSize = 5;

    /// <summary>
    /// Whether a page appears only once every world on it can be drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FALSE: each world is offered as soon as its build can draw it (Dan, 28
    /// September 2026). So the faithful build's second page opened with scuba and
    /// player in their 1987 art, and since 29 September holds all five hidden
    /// worlds in Aleksi Sahala's. VIP offers none of them until it has atlases
    /// for them (#45).
    /// </para>
    /// <para>
    /// This replaces the rule of 18 September - nothing on the second page until
    /// every world on it has art - which was set to keep borrowed art off the
    /// screen, at a time when none of the five was thought to have any. Scuba and
    /// player turned out to have their own, so the rule's purpose is met for them
    /// and holding it would only have kept them hidden behind three worlds that
    /// were never drawn. No world is drawn with another world's sheet either way.
    /// </para>
    /// <para>
    /// Here rather than in the browser because two things have to agree on it:
    /// the build that records a session and the inspector that replays one. A
    /// recording pins the world list it was made against, so a rule known to only
    /// one of them is a replay refused for no reason anyone could see.
    /// </para>
    /// </remarks>
    public const bool WholePagesOnly = false;

    /// <summary>The worlds a build offers under the standing rule.</summary>
    public static WorldEntry[] Offered(IEnumerable<WorldEntry> all, bool vip) =>
        Offered(all, vip, WholePagesOnly);

    /// <summary>
    /// The list a recording was made against, out of the full manifest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A build hands its host only what it can draw, and a recording pins that
    /// list, not the manifest's ten. So a replay has to be built over whichever
    /// build's list matches, or a session recorded in the browser is refused by
    /// the inspector reading the same data. Neither matching gives the faithful
    /// build's list, and the replay's own identity check then says why.
    /// </para>
    /// <para>
    /// The order is the faithful build's list, VIP's, then the 1988 five, then the
    /// faithful list again. The five are the manifest's first page, and every
    /// recording made before 28 September 2026 pinned exactly them, because that
    /// is all either build offered. They used to replay only because VIP's list
    /// happened to still be those five; the moment VIP offers a hidden world
    /// neither build's list is, and every earlier recording would be refused on a
    /// world-list mismatch. So they are a candidate of their own (#109), and
    /// giving VIP more worlds cannot break a recording made before it had them.
    /// </para>
    /// </remarks>
    public static WorldEntry[] AsRecorded(IEnumerable<WorldEntry> all, string worldsHash)
    {
        ArgumentNullException.ThrowIfNull(all);

        var manifest = all.ToList();
        var candidates = new[] { false, true }.Select(vip => Offered(manifest, vip)).ToList();
        candidates.Add([.. manifest.Take(PageSize)]);
        return candidates.FirstOrDefault(list => SessionIdentity.WorldsHashOf(list) == worldsHash)
               ?? candidates[0];
    }

    /// <summary>Which page a menu position is on.</summary>
    public static int PageOf(int index) => index / PageSize;

    /// <summary>How many pages <paramref name="worlds"/> worlds fill.</summary>
    public static int PageCount(int worlds) => (worlds + PageSize - 1) / PageSize;

    /// <summary>
    /// The select-screen picture for a page, relative to the build's root.
    /// </summary>
    /// <remarks>
    /// The faithful build has one picture, the original's, and paints each later
    /// page's portraits over its five boxes at run time. VIP bakes every page: the
    /// first is <c>vip/menu.png</c> and page N+1 is <c>vip/menu-N+1.png</c>, so the
    /// second is <c>vip/menu-2.png</c>, each with its own portraits drawn into the
    /// same five boxes. Painting the faithful build's 48 pixel portraits over VIP's
    /// large boxes stretched them, and the box is not square (#111).
    /// </remarks>
    public static string MenuPicture(bool vip, int page) =>
        !vip ? "game/menu_i.png" : page == 0 ? "vip/menu.png" : $"vip/menu-{page + 1}.png";

    /// <summary>
    /// Whether this page's portraits are already in its picture, so nothing is
    /// painted over it. Always for VIP; for the faithful build only on page one.
    /// </summary>
    public static bool PortraitsAreBaked(bool vip, int page) => vip || page == 0;

    /// <summary>Which box of its page a menu position sits in.</summary>
    public static int BoxOf(int index) => index % PageSize;

    /// <summary>
    /// The worlds this build offers, in menu order.
    /// </summary>
    /// <param name="all">The manifest, in menu order.</param>
    /// <param name="vip">Which build is asking.</param>
    /// <param name="wholePagesOnly">
    /// When true a page is offered only if every world on it can be drawn, so a
    /// page never appears with gaps in it. That was the rule set on 18 September
    /// 2026 - nothing hidden becomes reachable until every hidden world has its
    /// art - and it was set when none of the five was thought to have any. When
    /// false, each drawable world is offered on its own.
    /// </param>
    public static WorldEntry[] Offered(IEnumerable<WorldEntry> all, bool vip, bool wholePagesOnly)
    {
        ArgumentNullException.ThrowIfNull(all);

        var list = all.ToList();
        bool Drawable(WorldEntry w) => vip ? w.Vip : w.Original;

        if (!wholePagesOnly) return [.. list.Where(Drawable)];

        var offered = new List<WorldEntry>();
        for (var start = 0; start < list.Count; start += PageSize)
        {
            var page = list.Skip(start).Take(PageSize).ToList();
            if (page.All(Drawable)) offered.AddRange(page);
        }
        return [.. offered];
    }
}

/// <summary>
/// The five per-cave tables lifted out of the executable, one entry per record.
///
/// The addresses they came from are in <c>Rockford.Formats.CaveMetadata</c>, which is
/// what extracts them; this is only the shape they are shipped in.
/// </summary>
public sealed class CaveTables
{
    public int[] Required { get; set; } = [];
    public int[] Time { get; set; } = [];
    public int[] ScoreBefore { get; set; } = [];
    public int[] ScoreAfter { get; set; } = [];
    public int[] AmoebaCap { get; set; } = [];
}
