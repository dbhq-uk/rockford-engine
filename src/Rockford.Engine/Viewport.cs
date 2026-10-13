namespace Rockford.Engine;

/// <summary>
/// The window on the cave, ported from <c>0xFF87</c> and the framing at <c>0xFE6C</c>.
///
/// The game never showed a whole cave. Its renderer at <c>0x10E6E</c> walks 20
/// columns by 11 rows of a 40 by 22 cave and this decides which 20 by 11.
///
/// It is not a dead zone tested afresh each frame. It keeps a latch per direction,
/// so once it starts moving it keeps moving until the player is near the middle.
/// That hysteresis is the difference between a camera that drifts with you and one
/// that jitters every time you step over a boundary.
///
/// This is presentation state and lives outside <see cref="Simulation"/> on purpose:
/// what is visible must never reach the cell scan.
/// </summary>
public sealed class Viewport
{
    public const int Columns = 20;
    public const int Rows = 11;

    // Thresholds, in cells, converted from the byte and row offsets the original
    // compares. Horizontal values there are byte offsets, so 0x0A, 0x12 and 0x1C
    // are 5, 9 and 14 cells - easy to misread as 10, 18 and 28.
    private const int StartScrollingLeft = 5;    // 0x0A
    private const int StopScrolling = 9;         // 0x12
    private const int StartScrollingRight = 14;  // 0x1C

    // Vertical values are row offsets against a stride of 0x100, and two of the
    // three are half a row: 0x280 is two and a half rows, 0x780 is seven and a
    // half. Both comparisons are against a multiple of 0x100, so each half-row
    // threshold resolves to a whole row - but in opposite directions, which is
    // where this went wrong.
    //
    //   ffd5: cmp ax,0x280 / jge   -> scroll up while ax < 0x280, so dy <= 2
    //   ffeb: cmp ax,0x780 / jle   -> scroll down while ax > 0x780, so dy >= 8
    //
    // The port had `dy < 2`, which excludes the row the original includes. Two
    // rows from the top is inside the band, not on its edge.
    private const int StartScrollingUp = 2;      // 0x280, inclusive
    private const int StopScrollingVertical = 5; // 0x500
    private const int StartScrollingDown = 7;    // 0x780, exclusive

    // The origin clamps: 0x28 bytes is column 20, 0xB00 is row 11.
    private const int MaxColumn = 20;
    private const int MaxRow = 11;

    // ds:0x168E, 0x168C, 0x168A and 0x1690. Nothing outside 0xFF87 writes any of
    // them: there is no reset on a cave load, on a death or on a new game, so a
    // latch that was on when a cave ended is still on when the next one is framed.
    private bool _left, _right, _up, _down;

    /// <summary>Leftmost visible column.</summary>
    public int Column { get; private set; }

    /// <summary>
    /// Topmost visible row.
    ///
    /// Eleven, not zero. <c>0xED91</c> writes <c>ds:0x2248 = 0x0B00</c> - row
    /// eleven, column zero - once, before the first cave is even loaded, and
    /// nothing puts it back. So the very first cave is framed from the bottom of
    /// the cave rather than the top, and because the camera has hysteresis that is
    /// not the same place it would settle from row zero.
    /// </summary>
    public int Row { get; private set; } = 11;

    /// <summary>
    /// Frames a freshly loaded cave, from <c>0xFE74</c>.
    ///
    /// <code>
    /// fe8a: mov  ax,ds:0x2248
    /// fe8d: push ax
    /// fe8e: call 0xff87
    /// fe91: pop  ax
    /// fe92: cmp  ax,WORD PTR ds:0x2248
    /// fe96: jne  0xfe8a
    /// </code>
    ///
    /// It nudges the EXISTING origin until it stops moving. It does not reset it,
    /// and it does not reset the latches either - so where a cave opens depends on
    /// where the last one left the camera. The port cleared both and started from
    /// row zero, which frames a player at (10,7) on row zero where the original
    /// frames them on row two.
    ///
    /// One cave is framed outright instead, and that exception is the original's.
    /// </summary>
    public void Frame(int rawWorld, int stage, int playerColumn, int playerRow)
    {
        if (rawWorld == 4 && stage == 2)
        {
            // 0xFE82 writes ds:0x2248 = 0x0B0A - column 5 of row 11 - and jumps
            // past the settling loop entirely. The latches are left alone.
            Column = 5;
            Row = 11;
            return;
        }

        // The guard is ours. The original has no iteration cap, and with the
        // origin and the latches carried across there is nothing here that fails
        // to settle - but a bug elsewhere should hang a frame, not the browser.
        for (var guard = 0; guard < 64; guard++)
        {
            var was = (Column, Row);
            Follow(playerColumn, playerRow);
            if (was == (Column, Row)) break;
        }
    }

    /// <summary>One step of the camera, from <c>0xFF87</c>.</summary>
    public void Follow(int playerColumn, int playerRow)
    {
        var dx = playerColumn - Column;
        var dy = playerRow - Row;

        // Each latch turns on when the player nears its edge and off only once the
        // player is back near the middle. Both can never be on at once, because the
        // thresholds do not overlap.
        if (dx < StartScrollingLeft) _left = true;
        else if (dx >= StopScrolling) _left = false;

        if (dx > StartScrollingRight) _right = true;
        else if (dx <= StopScrolling) _right = false;

        if (dy <= StartScrollingUp) _up = true;
        else if (dy >= StopScrollingVertical) _up = false;

        if (dy > StartScrollingDown) _down = true;
        else if (dy <= StopScrollingVertical) _down = false;

        // Left wins over right and up over down, as the original's tests do.
        var moveX = _left ? -1 : _right ? 1 : 0;
        var moveY = _up ? -1 : _down ? 1 : 0;

        Column = Math.Clamp(Column + moveX, 0, MaxColumn);
        Row = Math.Clamp(Row + moveY, 0, MaxRow);
    }

    /// <summary>The cell address of a position within the window.</summary>
    public int CellAt(int x, int y) => CaveGrid.AddressOf(Column + x, Row + y);
}
