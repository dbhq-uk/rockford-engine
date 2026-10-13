namespace Rockford.Engine;

/// <summary>
/// How the original writes the four numbers on its status row.
///
/// The status row is not drawn by this port's canvas. The play field is 320 by 176
/// and the original's screen is 320 by 200 - the missing 24 pixels are three
/// character rows, and the HUD lives on the middle one. The port draws the field and
/// puts the same numbers in the page around it, which is why this file is formatting
/// and not rendering.
///
/// It is in the engine rather than a front end because the formats are rules read off
/// the disassembly, and both presentations have to agree on them.
///
/// <code>
/// 1220d: mov  ah,0x2            ; set the cursor from ds:0x224e and ds:0x2250
/// 12212: ...
/// 12228: mov  ah,0xe            ; then teletype the character in al
/// 1222c: mov  bl,BYTE PTR ds:0x1628
/// 12230: int  0x10
/// </code>
///
/// Every field goes through that one routine, a character at a time, with the column
/// in <c>ds:0x224E</c> incremented between them. There are exactly four placements on
/// row 1 in the whole executable, and no labels at all:
///
/// | Column | Field | Chars | Colour |
/// |---|---|---|---|
/// | 1 | spare lives, and only when there are five or more | 3 | <c>ds:0x162C</c> |
/// | 15 | treasure still needed | 3 | <c>ds:0x162C</c> |
/// | 24 | score | 6 | <c>ds:0x162A</c> |
/// | 35 | seconds left | 3 | <c>ds:0x1626</c> |
///
/// The other fourteen cursor placements in the binary are all row 6 or 7, which is
/// the menu and the dialogue boxes rather than the HUD.
/// </summary>
public static class HudFormat
{
    /// <summary>
    /// The score, from <c>0x12261</c> to <c>0x122DF</c>.
    ///
    /// <code>
    /// 12267: mov  bx,WORD PTR ds:0x1e00   ; the carry count
    /// 1226b: cmp  bx,0x63
    /// 1226e: jle  0x12273
    /// 12270: mov  bx,0x63                 ; clamped to 99
    /// 12273: mov  al,0x30                 ; then two digits of it
    /// ...
    /// 12293: mov  bx,WORD PTR ds:0x225c   ; and four of the points
    /// </code>
    ///
    /// Six digits, and this is the field the split score is FOR: the two words are
    /// not an arbitrary way to hold a number, they are the two halves of the
    /// display. Two digits come from the carry count, in units of ten thousand, and
    /// four from the points below it.
    ///
    /// <b>It is the one field with no blanking.</b> The other three replace a
    /// leading zero with a space; this one counts up from <c>'0'</c> and prints
    /// whatever it reaches, so a score of 450 reads <c>000450</c>. That asymmetry is
    /// deliberate and a player sees it, so the port keeps it.
    ///
    /// <b>And it saturates.</b> The clamp at <c>0x1226B</c> is on the carry count,
    /// not the score, so the display sticks at <c>999999</c> while the real score
    /// carries on underneath - and the bonus lives keep arriving, because
    /// <c>0x12244</c> reads the same counter the clamp never writes back to.
    /// </summary>
    public static string Score(int carries, int points) =>
        $"{Math.Min(carries, MaxCarriesShown):D2}{points:D4}";

    /// <summary>The largest carry count the score field can show, from the clamp at
    /// <c>0x1226B</c>. Ninety-nine of ten thousand, so the field stops at
    /// 999,999.</summary>
    public const int MaxCarriesShown = 99;

    /// <summary>
    /// Spare lives, from <c>0x13891</c>.
    ///
    /// <code>
    /// 1387c: cmp  WORD PTR ds:0x2258,0x4
    /// 13881: jg   0x13886
    /// 13883: jmp  0x138ff                 ; four or fewer: draw icons instead
    /// 13891: mov  bx,WORD PTR ds:0x2258
    /// 13895: dec  bx
    /// </code>
    ///
    /// <b>The number is one less than the lives.</b> It counts the spares, not the
    /// life being used - so a fresh game, which <c>0xED23</c> starts with five,
    /// shows four.
    ///
    /// The original only prints it at five or more. Below that it blits up to three
    /// 16 by 16 sprites into the status strip instead, at <c>0x1391B</c>, one for
    /// each spare - which is why it needs a number at all, and why the number
    /// appears exactly where the sprites run out. Those sprites are in the strip the
    /// canvas does not draw, so the port shows the count at every value; a HUD that
    /// went blank below five would be worse than either original behaviour.
    /// </summary>
    public static int SpareLives(int lives) => lives - 1;

    // The other two fields are NOT formatted here, and that is a decision rather
    // than an omission.
    //
    // The quota at 0x130F2 and the clock at 0x122F6 are the same eight instructions
    // twice, and both write three characters whose hundreds digit is a SPACE rather
    // than a zero - 0x122F6 loads 0x20, not 0x30 - so nine seconds reads " 09".
    //
    // That space is a text-mode alignment artefact. BIOS teletype output has no way
    // to right-align a field, so the original pads it by hand. The HUD here is DOM
    // with font-variant-numeric: tabular-nums, which holds the column width by
    // itself - the same result by a different mechanism. Reproducing the space would
    // be copying the mechanism and not the behaviour, and it would put a visible gap
    // in front of the number for no reason a player could read.
    //
    // The score above is different, and is padded, because its leading zeros are not
    // an alignment artefact: that field has no blanking at all, so 000450 is what
    // the original genuinely shows.
    //
    // Two things worth keeping from reading those routines. The hundreds digit is a
    // single subtract rather than a loop, so it can only ever be a space or a '1' -
    // 200 or more would carry into the tens loop at 0x1230B and print punctuation
    // where a digit belongs. No shipped cave can reach it: the longest in the table
    // at 0x26A2 is 180, 0xFEAF adds one, and the most time bonuses authored into one
    // cave is two, so the highest reachable clock in the forty is 191. That is
    // asserted against the real files in GameDataTests.
}
