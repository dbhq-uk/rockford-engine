using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The camera, from <c>0xFF87</c>. These pin the hysteresis, because a stateless
/// dead-zone reading of that routine produces a camera that jitters on every step
/// across a boundary and looks nothing like the original.
/// </summary>
public class ViewportTests
{
    [Fact]
    public void The_camera_starts_at_the_bottom_of_the_cave()
    {
        // 0xED91 writes ds:0x2248 = 0x0B00 - row eleven, column zero - once,
        // before the first cave is loaded, and nothing ever puts it back.
        var v = new Viewport();

        Assert.Equal(0, v.Column);
        Assert.Equal(11, v.Row);
    }

    [Fact]
    public void Where_a_cave_opens_depends_on_where_the_camera_already_was()
    {
        // The issue's own example, and the reason resetting to row zero is not
        // equivalent to starting from row eleven: the camera has hysteresis, so
        // the origin it settles to depends on the origin it settled FROM.
        //
        // From row eleven, a player at (10,7) pulls the camera up until dy
        // reaches five, which lands it on row two. From row zero, dy is already
        // seven and nothing moves at all.
        var fresh = new Viewport();
        fresh.Frame(rawWorld: 0, stage: 0, playerColumn: 10, playerRow: 7);
        Assert.Equal(2, fresh.Row);

        // Drive a second camera to row zero, then frame the same cave on it.
        // 0xFE8A settles the EXISTING origin; it does not reset it.
        var carried = new Viewport();
        carried.Frame(rawWorld: 0, stage: 0, playerColumn: 10, playerRow: 0);
        Assert.Equal(0, carried.Row);

        carried.Frame(rawWorld: 0, stage: 0, playerColumn: 10, playerRow: 7);

        Assert.Equal(0, carried.Row);
    }

    [Fact]
    public void Framing_a_cave_leaves_the_latches_where_they_were()
    {
        // The origin and the latches are two separate omissions, and the test
        // above only catches the origin: in that one the second frame supplies a
        // dy of seven, which clears the up latch on its own.
        //
        // Here the camera arrives at the frame already scrolling up, and the
        // player is close enough that nothing would START it scrolling. Only a
        // latch that survived can move it.
        var v = new Viewport();
        v.Follow(10, 11);
        Assert.Equal(10, v.Row);          // moving up, and still latched

        v.Frame(rawWorld: 0, stage: 0, playerColumn: 10, playerRow: 14);

        Assert.Equal(9, v.Row);           // cleared latches would leave it on ten
    }

    [Fact]
    public void Two_rows_from_the_top_is_inside_the_band_not_on_its_edge()
    {
        // 0xFFD5 compares against 0x280 - two and a half rows - and scrolls while
        // the difference is BELOW it. Every comparison is a multiple of 0x100, so
        // that resolves to dy <= 2. The port had dy < 2 and waited a row too long.
        var v = new Viewport();
        v.Frame(rawWorld: 0, stage: 0, playerColumn: 10, playerRow: 12);
        Assert.Equal(7, v.Row);      // settled with the player five rows down

        // Walking up. Nothing happens until the player is two rows from the top.
        v.Follow(10, 11);
        Assert.Equal(7, v.Row);
        v.Follow(10, 10);
        Assert.Equal(7, v.Row);

        v.Follow(10, 9);             // dy is two now

        Assert.Equal(6, v.Row);
    }

    [Fact]
    public void A_fresh_cave_frames_the_player()
    {
        var v = new Viewport();
        v.Frame(rawWorld: 0, stage: 0, playerColumn: 30, playerRow: 18);

        // The player has to be inside the window, and not jammed against an edge.
        Assert.InRange(30 - v.Column, 0, Viewport.Columns - 1);
        Assert.InRange(18 - v.Row, 0, Viewport.Rows - 1);
    }

    [Fact]
    public void One_cave_is_framed_outright_rather_than_settled()
    {
        // 0xFE74: raw world 4, stage 2 gets ds:0x2248 = 0x0B0A directly.
        var v = new Viewport();
        v.Frame(rawWorld: 4, stage: 2, playerColumn: 0, playerRow: 0);

        Assert.Equal(5, v.Column);
        Assert.Equal(11, v.Row);
    }

    [Fact]
    public void The_camera_does_not_move_while_the_player_is_near_the_middle()
    {
        var v = new Viewport();
        v.Frame(rawWorld: 0, stage: 0, playerColumn: 20, playerRow: 12);

        var (c, r) = (v.Column, v.Row);
        for (var i = 0; i < 5; i++) v.Follow(20, 12);

        Assert.Equal(c, v.Column);
        Assert.Equal(r, v.Row);
    }

    [Fact]
    public void Once_it_starts_moving_it_keeps_going_until_the_player_is_centred()
    {
        var v = new Viewport();
        v.Frame(rawWorld: 0, stage: 0, playerColumn: 20, playerRow: 12);
        var start = v.Column;

        // Step the player to the right edge of the window once. A dead-zone camera
        // would move a single column and stop; this one carries on.
        v.Follow(v.Column + 15, 12);
        Assert.Equal(start + 1, v.Column);

        // With the player standing still, it should still be catching up - and it
        // should STOP in the right place. "More than one column" was all this
        // used to ask, so deleting the statement that clears the right latch
        // passed: the camera simply ran on to the cave's clamp instead.
        var target = v.Column + 15;
        for (var i = 0; i < 40; i++) v.Follow(target, 12);

        // 0xFFBC clears the right latch once the player is nine columns in, so
        // that is where it comes to rest - not against the edge of the cave.
        Assert.Equal(9, target - v.Column);

        // And stays there.
        var settled = v.Column;
        for (var i = 0; i < 5; i++) v.Follow(target, 12);
        Assert.Equal(settled, v.Column);
    }

    [Fact]
    public void The_window_never_runs_off_the_cave()
    {
        var v = new Viewport();
        v.Frame(rawWorld: 0, stage: 0, playerColumn: 0, playerRow: 0);

        for (var i = 0; i < 80; i++) v.Follow(0, 0);
        Assert.Equal(0, v.Column);
        Assert.Equal(0, v.Row);

        for (var i = 0; i < 80; i++) v.Follow(CaveGrid.Width - 1, CaveGrid.Height - 1);
        Assert.Equal(CaveGrid.Width - Viewport.Columns, v.Column);
        Assert.Equal(CaveGrid.Height - Viewport.Rows, v.Row);
    }

    [Fact]
    public void Both_axes_can_move_on_the_same_step()
    {
        var v = new Viewport();
        v.Frame(rawWorld: 0, stage: 0, playerColumn: 20, playerRow: 12);
        var (c, r) = (v.Column, v.Row);

        v.Follow(v.Column + 16, v.Row + 9);

        Assert.Equal(c + 1, v.Column);
        Assert.Equal(r + 1, v.Row);
    }
}
