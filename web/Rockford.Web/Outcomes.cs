namespace Rockford.Web;

/// <summary>A class for the overlay's styling, a word, and an optional second line.</summary>
public readonly record struct Outcome(string Kind, string Word, string? Note);

/// <summary>
/// What the outcome overlay says when a cave ends (#144).
///
/// The host's "out" is a cave that was COMPLETED, not a timeout: it is set when
/// <c>Sim.CaveComplete</c> is true. The overlay used to word it "out of time", so a
/// player who had just won was told they had run out. A clock-out is a death like
/// any other and arrives as "caught"; the clock tells it apart, which is the
/// original's own test (<c>jle</c> at <c>0x1E08F</c>, see docs/FORMATS.md).
///
/// Deliberately NOT a catch-all: an unrecognised message stays in the status row
/// rather than being promoted to a full-screen event because it happened to be set.
/// </summary>
public static class Outcomes
{
    public static Outcome? For(string? message, int timeRemaining, int score) => message switch
    {
        "out" => new Outcome("clear", "cave clear", null),
        "caught" when timeRemaining <= 0 => new Outcome("time", "out of time", "a life gone"),
        "caught" => new Outcome("caught", "caught", "a life gone"),
        "game over" => new Outcome("over", "game over", $"score {score}"),
        _ => null,
    };
}
