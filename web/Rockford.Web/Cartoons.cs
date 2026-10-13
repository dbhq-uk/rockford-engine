namespace Rockford.Web;

/// <summary>Which of a world's three cartoons plays, and the order they sit in on its sheet.</summary>
public enum CartoonKind
{
    None = 0,
    Win = 1,
    Lose = 2,
    Time = 3,
}

/// <summary>
/// The original's cartoons: a 48 by 48 window of six frames that plays when a cave
/// ends. Which one is decided here; how long it runs, where it sits and how it is
/// drawn is <c>cartoon.js</c>, and what the original does is in
/// <c>docs/FORMATS.md</c>, "When each cartoon plays".
/// </summary>
public static class Cartoons
{
    /// <summary>
    /// The file a build plays a world's cartoons from. The faithful build has the
    /// original's strip, <c>hunter-cartoon.png</c>; VIP has the redrawn one at eight
    /// to one, <c>hunter-cartoon-x8.png</c>, named as its atlases are. VIP ships no
    /// file of the original's.
    /// </summary>
    public static string StripFor(string strip, bool vip) =>
        vip ? $"{Path.GetFileNameWithoutExtension(strip)}-x8.png" : strip;

    /// <summary>
    /// What the host just reported, as a cartoon.
    ///
    /// A won cave is WIN. A death is LOSE while the clock is above zero and TIME
    /// when it is not - <c>0x1E08F</c> is a <c>jle</c>, so exactly zero is TIME.
    /// Running out of time kills the player the same way any death does, which is
    /// why it arrives here as "caught" with the clock at nothing.
    ///
    /// The clock, not <c>Simulation.TimeExpired</c>: that flag is cleared again on
    /// the next step and the eight-frame death countdown takes eight steps, so by
    /// the time the host reports the death it is long false.
    /// </summary>
    public static CartoonKind For(string? message, int timeRemaining) => message switch
    {
        "out" or "world complete" => CartoonKind.Win,
        "caught" or "game over" => timeRemaining <= 0 ? CartoonKind.Time : CartoonKind.Lose,
        _ => CartoonKind.None,
    };
}
