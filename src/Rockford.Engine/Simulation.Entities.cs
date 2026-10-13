namespace Rockford.Engine;

public sealed partial class Simulation
{
    /// <summary>The tag the top six bits of a facing byte always carry. Only the low
    /// two bits mean anything; the handlers mask with 3 on read and put this back on
    /// write, so a stored facing is only ever <c>0xF8</c> to <c>0xFB</c>.</summary>
    public const byte FacingTag = 0xF8;

    // Runtime direction numbers, proved twice: by which move routine the dispatcher at
    // 0x11BCE picks, and by the delta table at ds:0x1656 holding -2, -0x100, +2, +0x100.
    public const int DirLeft = 0;
    public const int DirUp = 1;
    public const int DirRight = 2;
    public const int DirDown = 3;

    /// <summary>Treasure still needed before the exit opens. <c>ds:0x164A</c>.</summary>
    public int TreasureQuota { get; set; }

    /// <summary>Frames elapsed. Incremented after the scan, as at <c>0xEDD5</c>, which
    /// is why the open exit animates on odd passes. <c>ds:0x16C2</c>.</summary>
    public int TickCounter { get; private set; }

    /// <summary>True once the player has stepped onto an open exit. <c>ds:0x166E</c>.</summary>
    public bool CaveComplete { get; set; }

    // ---- enemies ----------------------------------------------------------------

    /// <summary>
    /// The two enemy families, from <c>0x11B54</c> and <c>0x11B91</c>. One body, one
    /// sign flip. The clockwise family explodes into treasure, the other into space.
    /// </summary>
    private void Enemy(int si, int turn, int kind)
    {
        // Attempt one: turn, then try. Attempt two: turn back to where it was facing
        // and try straight on. If both fail it turns once more the same way as the
        // second turn and makes NO third attempt, which is the part the old engine had
        // wrong - it tried three directions and then reversed.
        var dir = (_grid.StateAt(si) + turn) & 3;
        SetFacing(si, dir);
        if (TryMove(si, dir, kind)) return;

        dir = (dir - turn) & 3;
        SetFacing(si, dir);
        if (TryMove(si, dir, kind)) return;

        dir = (dir - turn) & 3;
        SetFacing(si, dir);
    }

    private void SetFacing(int si, int dir) => _grid[si + 1] = (byte)(FacingTag | dir);

    /// <summary>True means stop trying: it either moved or died. From <c>0x11BCE</c>.</summary>
    private bool TryMove(int si, int dir, int kind) => dir switch
    {
        DirLeft => Step(si, si - CaveGrid.CellStride, bump: false),
        DirRight => Step(si, si + CaveGrid.CellStride, bump: true),
        DirUp => MoveUp(si, kind),
        DirDown => Step(si, si + CaveGrid.RowStride, bump: true),
        _ => false,
    };

    /// <summary>
    /// Moving up, from <c>0x12099</c>. It is the only direction with a pre-check, and
    /// the pre-check contains a genuine bug in the original that the port keeps.
    /// </summary>
    private bool MoveUp(int si, int kind)
    {
        var right = _grid.ValueAt(si + CaveGrid.CellStride);
        var above = _grid.ValueAt(si - CaveGrid.RowStride);

        // The first two tests read the cell above. The third reads the cell to the
        // RIGHT, because 0x1209B loaded the right neighbour into AL and the amoeba test
        // at 0x120AC compares AL rather than AH. So an enemy facing up with an amoeba
        // beside it destroys itself whatever is overhead. That is what shipped.
        if (above == RuntimeTile.BoulderFalling
            || above == RuntimeTile.TreasureFalling
            || RuntimeTile.IsAmoeba(right))
        {
            Blast(si - BlastCentreOffset, kind);
            return true;
        }

        return Step(si, si - CaveGrid.RowStride, bump: false);
    }

    /// <summary>
    /// One move. The destination must be plain space: enemies cannot dig.
    /// <paramref name="bump"/> adds one to the value, making it the just-moved state,
    /// and is used for right and down only because those land ahead of the scan.
    /// </summary>
    private bool Step(int src, int dst, bool bump)
    {
        if (_grid.ValueAt(dst) != RuntimeTile.Space) return false;

        var a = _grid.ReadWord(src);
        var b = _grid.ReadWord(src + CaveGrid.ShadowPlane);

        _grid.WriteWord(src, 0xFF00);
        _grid.WriteWord(src + CaveGrid.ShadowPlane, 0xFFFF);
        _grid.WriteWord(dst, bump ? (ushort)(a + 1) : a);
        _grid.WriteWord(dst + CaveGrid.ShadowPlane, b);

        // Every enemy move in the game goes through here, both families and all
        // four directions, so one report covers the lot. The value is the one it
        // had before the bump - the just-moved state is a scan-order marker and
        // not a different creature.
        Observe(Observation.Moved(src, dst, (byte)(a & 0xFF)));

        return true;
    }

    // ---- expanding wall ---------------------------------------------------------

    private const byte RightLatched = 0x01;
    private const byte LeftLatched = 0x02;

    /// <summary>
    /// Ticks since the conversion window opened, the way the original computes it.
    ///
    /// <code>
    /// mov ax,ds:0x16a8
    /// sub ax,WORD PTR ds:0x1cf4
    /// cmp ax,0xe10
    /// jg  ...
    /// </code>
    ///
    /// Sixteen bits wide and read as SIGNED, which is not an implementation
    /// detail. Two things fall out of it.
    ///
    /// A cave that runs past 32,768 ticks - nine minutes, which a 999 second
    /// limit allows - wraps the difference negative and reopens the window.
    ///
    /// It cannot go negative any other way. The loader clears the latch at
    /// <c>0x108A4</c> on every cave load, and the next thing to fall into a wall
    /// stamps the timestamp before anything reads it - so the difference only ever
    /// starts at zero. An int subtraction would have the wall dead for the rest of
    /// a long cave instead of coming back.
    /// </summary>
    private int TimedWallElapsed() => (short)(ushort)(_interruptTicks - TimedWallStart);

    /// <summary>True while a boulder or a gem falling into one of these walls
    /// converts rather than lands. <c>0x13F7E</c>, <c>0x141D1</c>, <c>0x14690</c>.</summary>
    private bool TimedWallConverting() => TimedWallElapsed() <= TimedWallTicks;

    /// <summary>
    /// The timed converting wall's own handler, <c>0x1467F</c>.
    ///
    /// <code>
    /// 1467f: cmp  BYTE PTR ds:0x1696,0xff
    /// 14684: jne  0x1469e
    /// 14686: mov  ax,ds:0x16a8
    /// 14689: sub  ax,WORD PTR ds:0x1cf4
    /// 1468d: cmp  ax,0xe10
    /// 14690: jg   0x14694
    /// 14692: inc  WORD PTR [si]
    /// 14694: cmp  WORD PTR [si],0xff77
    /// 14698: jbe  0x1469e
    /// 1469a: mov  WORD PTR [si],0xff74
    /// </code>
    ///
    /// The wall cycles through its four cells while the window is open, which is
    /// how a player can see that it is live. The port never dispatched this range
    /// at all, so a wall a boulder had activated sat on <c>0x74</c> for the rest of
    /// the cave.
    ///
    /// Nothing here turns the wall into anything else. Past the window it simply
    /// stops animating and stays a wall - it does not become brick, and it is not
    /// latched shut either, because the window can reopen.
    /// </summary>
    private void TimedWall(int si)
    {
        if (!TimedWallLatched) return;                 // 0x14684

        // A whole-word increment, as the original does: the state byte rides along,
        // which is harmless here because these cells are only ever 0xFF74 to 0xFF77.
        if (TimedWallConverting())                      // 0x14690
            _grid.WriteWord(si, (ushort)(_grid.ReadWord(si) + 1));

        // Unsigned, and outside the window test - so a cell that has run past the
        // last of the four comes back whether or not the window is open.
        if (_grid.ReadWord(si) > 0xFF77)                // 0x14698
            _grid.WriteWord(si, 0xFF74);                // 0x1469A
    }

    /// <summary>
    /// The two dormant timed entities, from <c>0x13ED8</c> and <c>0x14613</c>.
    ///
    /// <code>
    /// 13ed8: cmp  WORD PTR ds:0x16aa,0x4b0
    /// 13ede: jl   0x13eec
    /// 13ee0: mov  WORD PTR [si],0xffa0     ; hatch
    /// 13ee4: mov  WORD PTR ds:0x1612,0x1
    /// 13eea: jmp  0x13ef6
    /// 13eec: inc  al                       ; or just animate
    /// 13eee: cmp  al,0x7b
    /// 13ef0: jbe  0x13ef4
    /// 13ef2: mov  al,0x78
    /// 13ef4: mov  BYTE PTR [si],al
    /// </code>
    ///
    /// Two handlers, one shape. Below 1200 interrupts the cell cycles its four
    /// frames - the VALUE byte only, so whatever the loader put in the state byte
    /// survives - and at 1200 it hatches into the worm proper.
    ///
    /// The port had the arming and neither the timer nor this, so an authored worm
    /// sat on its first frame for the whole cave and nothing ever came out of it.
    /// </summary>
    private void TimedEntity(int si, byte value, int timer, byte first, byte last, byte hatched)
    {
        if (timer >= TimedEntityHatchTicks)
        {
            _grid.WriteWord(si, (ushort)(0xFF00 | hatched));

            // 0x13EE4. The worm starts one cell long, and its own routine grows it.
            if (hatched == RuntimeTile.WormA) WormALength = 1;
            else WormBLength = 1;
            return;
        }

        var next = (byte)(value + 1);
        _grid[si] = next > last ? first : next;
    }

    /// <summary>
    /// Interrupts before a dormant entity hatches - 1200, which is twenty seconds.
    /// <c>0x13EDE</c> and <c>0x14619</c>.
    /// </summary>
    public const int TimedEntityHatchTicks = 0x4B0;

    /// <summary>
    /// Expanding wall, runtime <c>0x15</c>, handler <c>0x14632</c>. Right is tried
    /// before left.
    ///
    /// The two low bits of the state byte remember whether each side was blocked last
    /// time. A side that has just become free spends one visit clearing its bit, so
    /// there is always a one-tick pause before it grows. A freshly loaded wall has both
    /// bits set and therefore cannot grow on its first visit.
    /// </summary>
    private void ExpandingWall(int si)
    {
        if (_grid.ValueAt(si + CaveGrid.CellStride) == RuntimeTile.Space)
        {
            if ((_grid.StateAt(si) & RightLatched) == 0)
            {
                // Rightward growth lands ahead of the scan, so it is written deferred.
                _grid.WriteWord(si + CaveGrid.CellStride, 0xFF16);
                _soundFlags |= 0x20;
            }
            else
            {
                _grid[si + 1] &= unchecked((byte)~RightLatched);
            }
        }
        else
        {
            _grid[si + 1] |= RightLatched;
        }

        if (_grid.ValueAt(si - CaveGrid.CellStride) == RuntimeTile.Space)
        {
            if ((_grid.StateAt(si) & LeftLatched) == 0)
            {
                // Leftward growth lands behind the scan and can be live at once. Its
                // state byte marks the right side latched, because the parent is there.
                _grid.WriteWord(si - CaveGrid.CellStride, 0xFD15);
                _soundFlags |= 0x20;
            }
            else
            {
                _grid[si + 1] &= unchecked((byte)~LeftLatched);
            }
        }
        else
        {
            _grid[si + 1] |= LeftLatched;
        }
    }

    // ---- exit -------------------------------------------------------------------

    /// <summary>Closed exit, runtime <c>0x07</c>, handler <c>0x14316</c>. Opens on the
    /// first scan where the quota is exactly zero, and never closes again.</summary>
    private void ExitClosed(int si)
    {
        if (TreasureQuota == 0)
            _grid.WriteWord(si, 0xFF30);
    }

    /// <summary>
    /// Open exit, runtime <c>0x30</c> to <c>0x33</c>, handler <c>0x14301</c>. Pure
    /// animation, one frame every other tick. All four frames are walkable and all four
    /// finish the cave.
    /// </summary>
    private void ExitOpen(int si)
    {
        if ((TickCounter & 1) != 0)
            _grid.WriteWord(si, (ushort)(_grid.ReadWord(si) + 1));

        if (_grid.ReadWord(si) > 0xFF33)
            _grid.WriteWord(si, 0xFF30);
    }
}
