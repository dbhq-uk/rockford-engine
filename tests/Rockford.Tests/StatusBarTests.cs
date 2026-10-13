using Rockford.Engine;
using Rockford.Web.Shared;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The status row's markup, rendered with bUnit.
///
/// These are the tests issue #24 was really about. The engine's rules were covered
/// and the page that shows them was not, so <c>@Session.SpareLives</c> could go back
/// to <c>@Session.Lives</c> and nothing would fail - which is exactly the mistake
/// this row had in it until the HUD work.
///
/// <para>
/// Microsoft's guidance is to pick the test scope per scenario rather than per
/// project: a component with no JS interop has "no disadvantages to choosing unit
/// testing", while one whose behaviour depends on JS manipulating the DOM wants an
/// end-to-end test. This row is the first kind. The canvas, the sprite atlases and
/// the Web Audio graph are the second, and they are covered by
/// <c>tools/jslifecycle.mjs</c> instead - the JS tested as JS, which is the same
/// guidance's advice for a component whose JS is large and whose interface to it is
/// small.
/// </para>
/// </summary>
public class StatusBarTests : BunitContext
{
    /// <summary>A row mid-game, with every field at a value a test can see.</summary>
    private static StatusRow Sample(
        bool playing = true,
        int stage = 1,
        int spareLives = 4,
        int collected = 3,
        int needed = 12,
        string score = "000450",
        int time = 95,
        bool exitOpen = false,
        string? message = null) =>
        new(playing, "hunter", stage, "GOLD", spareLives, collected, needed,
            score, time, exitOpen, message);

    private IRenderedComponent<StatusBar> Row(StatusRow row) =>
        Render<StatusBar>(p => p.Add(c => c.Row, row));

    private IRenderedComponent<StatusBar> Row(
        bool playing = true,
        int spareLives = 4,
        int collected = 3,
        int needed = 12,
        string score = "000450",
        int time = 95,
        bool exitOpen = false,
        string? message = null) =>
        Row(Sample(playing, 1, spareLives, collected, needed, score, time, exitOpen, message));

    // ---- what it shows ----------------------------------------------------

    [Fact]
    public void It_shows_every_number_the_original_puts_on_row_one()
    {
        var row = Row();
        var text = row.Find(".hud").TextContent;

        Assert.Contains("hunter", text);
        Assert.Contains("2", text);                 // stage 1, counted from one
        Assert.Contains("spare", text);
        Assert.Contains("4", text);
        Assert.Contains("GOLD", text);
        Assert.Contains("3", text);
        Assert.Contains("12", text);
        Assert.Contains("000450", text);
        Assert.Contains("95", text);
    }

    [Fact]
    public void The_score_is_shown_exactly_as_it_is_given()
    {
        // Already formatted when it arrives, because six-digit zero padding is a
        // rule off the disassembly rather than a choice this component makes. A row
        // that reformatted it could quietly drop the padding.
        var row = Row(score: HudFormat.Score(0, 450));

        Assert.Equal("000450", Value(row, "score"));
    }

    [Fact]
    public void The_lives_shown_are_the_spares_and_are_not_recomputed_here()
    {
        // The whole point of the bug this row had: 0x13895 decrements before
        // drawing, so a fresh game shows four. The component is handed the four -
        // it does not get to decide - and this is what catches it being handed
        // Lives instead.
        var session = new GameSession();
        var row = Row(spareLives: session.SpareLives);

        Assert.Equal("4", Value(row, "spare"));
        Assert.Equal(5, session.Lives);
    }

    [Fact]
    public void The_cave_number_is_counted_from_one()
    {
        var row = Row(Sample(stage: 3));

        Assert.Contains("4", FieldFor(row, "world").TextContent);
    }

    // ---- when it warns ----------------------------------------------------

    [Theory]
    [InlineData(10, false)]
    [InlineData(9, true)]
    [InlineData(0, true)]
    public void The_clock_turns_red_where_the_game_itself_starts_warning(int time, bool low)
    {
        // Simulation.WarningSeconds is where the original's own rising tone begins.
        // The row says the same thing, and the boundary is under rather than at.
        var row = Row(time: time);
        var span = FieldFor(row, "time");

        Assert.Equal(low, span.ClassList.Contains("low"));
    }

    [Fact]
    public void The_last_life_is_called_out()
    {
        // Zero spares is the last life, which is the only number on the row whose
        // value changes what the player should do next.
        // And it SAYS so. "lives 0" beside a living player was faithful
        // arithmetic behind a label that read as a contradiction.
        var last = FieldFor(Row(spareLives: 0), "last life");
        Assert.True(last.ClassList.Contains("low"));

        Assert.False(FieldFor(Row(spareLives: 1), "spare").ClassList.Contains("low"));
    }

    [Fact]
    public void The_exit_says_which_it_is_and_lights_when_it_opens()
    {
        var shut = Row(exitOpen: false).Find(".hud-exit");
        Assert.Contains("exit shut", shut.TextContent);
        Assert.False(shut.ClassList.Contains("lit"));

        var open = Row(exitOpen: true).Find(".hud-exit");
        Assert.Contains("exit open", open.TextContent);
        Assert.True(open.ClassList.Contains("lit"));
    }

    // ---- when it says nothing ---------------------------------------------

    [Fact]
    public void There_is_no_row_at_all_off_the_playing_screen()
    {
        // The title and select screens are whole pictures in the original, with no
        // status row over them.
        var row = Row(playing: false);

        Assert.Empty(row.FindAll(".hud .field"));
        Assert.Empty(row.FindAll(".hud-exit"));
    }

    [Fact]
    public void The_message_appears_only_when_there_is_one()
    {
        Assert.Empty(Row(message: null).FindAll(".msg"));

        var other = Row(message: "world complete");
        Assert.Equal("world complete", other.Find(".msg").TextContent);
    }

    [Theory]
    [InlineData("caught")]
    [InlineData("out")]
    [InlineData("game over")]
    public void An_outcome_the_overlay_shows_is_not_repeated_here(string message)
    {
        // #35 promoted these three to overlays over the cave. Showing them in
        // the status row as well would say the same thing twice, in two
        // weights, in the same moment.
        Assert.Empty(Row(message: message).FindAll(".msg"));
    }

    // ---- it updates ------------------------------------------------------

    // ---- the hierarchy ----------------------------------------------------

    [Fact]
    public void The_quota_and_the_clock_are_the_primary_pair()
    {
        // #35. Four fields at one weight meant the eye had to read all four to
        // find the two that decide what to do next, while a boulder was falling.
        var row = Row();
        var primary = row.Find(".hud-primary").TextContent;

        Assert.Contains("GOLD", primary);
        Assert.Contains("time", primary);

        var secondary = row.Find(".hud-secondary").TextContent;
        Assert.Contains("score", secondary);
        Assert.Contains("world", secondary);
        Assert.DoesNotContain("time", secondary);
    }

    [Theory]
    [InlineData(0, 12, 0)]
    [InlineData(3, 12, 25)]
    [InlineData(12, 12, 100)]
    [InlineData(15, 12, 100)]      // the quota can be overshot
    [InlineData(5, 0, 100)]        // and a cave can ask for none
    public void The_quota_bar_is_a_picture_of_the_two_numbers_beside_it(
        int collected, int needed, int percent)
    {
        // Computed from them, never tracked separately, so it cannot disagree
        // with them - and clamped, because a bar wider than its track draws
        // outside the HUD.
        var row = Row(collected: collected, needed: needed);

        Assert.Contains($"width: {percent}%", row.Find(".hud .fill").GetAttribute("style"));
    }

    [Fact]
    public void The_quota_is_marked_met_rather_than_only_counted()
    {
        Assert.False(FieldFor(Row(collected: 11, needed: 12), "GOLD").ClassList.Contains("met"));
        Assert.True(FieldFor(Row(collected: 12, needed: 12), "GOLD").ClassList.Contains("met"));
    }

    [Fact]
    public void A_new_value_reaches_the_markup()
    {
        // Ten steps a second, so a row that rendered once and stopped would be the
        // whole HUD broken in a way no static assertion would see.
        var row = Row(time: 95, score: "000000");

        row.Render(p => p.Add(c => c.Row, Sample(time: 94, score: "000030")));

        Assert.Equal("94", Value(row, "time"));
        Assert.Equal("000030", Value(row, "score"));
    }

    // ---- helpers ----------------------------------------------------------

    /// <summary>
    /// The element carrying a labelled field.
    /// </summary>
    /// <remarks>
    /// By its LABEL, not by a substring of the whole field. The old version
    /// searched every span for the text and took the first match, which with a
    /// hierarchy is ambiguous - "time" appears in the clock's label and nowhere
    /// else, but a value of 12 matches a field looking for "1".
    /// </remarks>
    private static AngleSharp.Dom.IElement FieldFor(IRenderedComponent<StatusBar> row, string label) =>
        row.FindAll(".hud .field")
            .First(f => f.QuerySelector(".label")!.TextContent.Trim() == label);

    /// <summary>The bold value inside a labelled field.</summary>
    private static string Value(IRenderedComponent<StatusBar> row, string label) =>
        FieldFor(row, label).QuerySelector("b")!.TextContent;
}
