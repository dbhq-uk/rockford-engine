namespace Rockford.Engine;

/// <summary>
/// The two taps and the two things they shoot.
///
/// Authored <c>38</c> is the fire tap and authored <c>C4</c> is the acid tap. Once
/// armed, each emits one projectile every eight steps: the fire tap upwards, the acid
/// tap downwards. The two are deliberately four steps out of phase, so a cave with
/// both never emits them on the same step.
///
/// Fire rises and acid falls, each one cell per four-state cycle. The interesting part
/// is what they do when something is in the way, and the two rules that make these
/// worth having at all:
///
/// - **Fire and acid destroy each other.** Fire that meets anything in the acid's own
///   state range takes a kind-2 blast with it.
/// - **Treasure struck by fire becomes a creature** - the clockwise family, which
///   yields treasure again when killed.
///
/// Fire that cannot rise turns sideways, and remembers which way it went in the cell's
/// state byte. Blocked both ways it usually burns out, with a one-in-four chance of
/// surviving where it is.
/// </summary>
public sealed partial class Simulation
{
    /// <summary>
    /// How often a tap emits, from <c>test $0x7</c> at <c>0x14460</c> and
    /// <c>0x140CE</c>. One step in eight.
    /// </summary>
    public const int TapPeriod = 8;

    /// <summary>
    /// What the fire tap adds to the counter before testing it, from
    /// <c>add $0x4,%ax</c> at <c>0x140C4</c>. It is half the period, so the two taps
    /// alternate rather than firing together.
    /// </summary>
    public const int FireTapPhase = 4;

    /// <summary>
    /// The acid tap, runtime <c>08</c>-<c>0B</c>, from <c>0x14445</c>.
    ///
    /// The emptiness test here reads the whole word, where the fire tap reads only the
    /// value byte. That is what the two routines actually do, so the port keeps the
    /// difference: a cell holding space with a non-<c>FF</c> state byte blocks acid but
    /// not fire.
    /// </summary>
    private void AcidTap(int addr, byte value)
    {
        if (!ProducerDownArmed) return;                      // 0x14445

        // 0x1444C: the last state wraps to the first, anything else counts up.
        if (value == RuntimeTile.AcidTapLast) _grid.WriteWord(addr, 0xFF00 | RuntimeTile.AcidTap);
        else _grid.WriteWord(addr, (ushort)(_grid.ReadWord(addr) + 1));

        var below = addr + CaveGrid.RowStride;
        if (_grid.ReadWord(below) != 0xFF00) return;         // 0x14458
        if ((TickCounter & (TapPeriod - 1)) != 0) return;    // 0x14460

        _grid.WriteWord(below, 0xFF00 | RuntimeTile.AcidHead);
    }

    /// <summary>The fire tap, runtime <c>0C</c>-<c>0F</c>, from <c>0x140AE</c>.</summary>
    private void FireTap(int addr, byte value)
    {
        if (!ProducerUpArmed) return;                        // 0x140AE

        if (value == RuntimeTile.FireTapLast) _grid.WriteWord(addr, 0xFF00 | RuntimeTile.FireTap);
        else _grid.WriteWord(addr, (ushort)(_grid.ReadWord(addr) + 1));

        var above = addr - CaveGrid.RowStride;
        if (_grid.ValueAt(above) != RuntimeTile.Space) return;             // 0x140C7
        if (((TickCounter + FireTapPhase) & (TapPeriod - 1)) != 0) return; // 0x140CE

        _grid.WriteWord(above, 0xFF00 | RuntimeTile.FireHead);
    }

    /// <summary>
    /// Acid's first terminal, runtime <c>23</c>, from <c>0x14322</c>.
    ///
    /// The blast anchor looks wrong at a glance and is not. <see cref="Blast"/> takes
    /// the upper-left of the 3x3 block and its centre is <see cref="BlastCentreOffset"/>
    /// further on, so passing <c>addr - CellStride</c> centres the blast on
    /// <c>addr + RowStride</c> - the creature directly below. Every falling-object
    /// caller in this engine subtracts the same two bytes for the same reason.
    /// </summary>
    private void AcidTerminal23(int addr)
    {
        var below = addr + CaveGrid.RowStride;

        if (_grid.ValueAt(below) == RuntimeTile.EnemyClockwise)             // 0x14322
        {
            Blast(addr - CaveGrid.CellStride, kind: 1);
            return;
        }

        if (_grid.ValueAt(below) == RuntimeTile.Space)                      // 0x14336
        {
            // A projectile advancing is a move like any other: the head leaves one
            // cell and appears in the next. It only happens once every four steps -
            // the states in between cycle in place - so most steps report nothing
            // for it, which is correct rather than a gap.
            _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Space);
            _grid.WriteWord(below, 0xFF00 | RuntimeTile.AcidHead);
            Observe(Observation.Moved(addr, below, RuntimeTile.AcidHead));
            return;
        }

        _grid.WriteWord(addr, 0xFF00 | RuntimeTile.AcidBlocked1);           // 0x14349
    }

    /// <summary>Runtime <c>24</c>, from <c>0x146A0</c>. Falls as a deferred seed.</summary>
    private void AcidTerminal24(int addr)
    {
        var below = addr + CaveGrid.RowStride;

        if (_grid.ValueAt(below) == RuntimeTile.Space)
        {
            _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Space);
            _grid.WriteWord(below, 0xFF00 | RuntimeTile.SeedAcidBlocked2);
            return;
        }

        _grid.WriteWord(addr, 0xFF00 | RuntimeTile.AcidBlocked2);
    }

    /// <summary>Runtime <c>25</c>, from <c>0x146B8</c>.</summary>
    private void AcidTerminal25(int addr)
    {
        var below = addr + CaveGrid.RowStride;

        if (_grid.ValueAt(below) == RuntimeTile.Space)
        {
            _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Space);
            _grid.WriteWord(below, 0xFF00 | RuntimeTile.AcidHead);
            Observe(Observation.Moved(addr, below, RuntimeTile.AcidHead));
            return;
        }

        _grid.WriteWord(addr, 0xFF00 | RuntimeTile.AcidSettled);
    }

    /// <summary>
    /// Acid's second terminal, runtime <c>2B</c>, from <c>0x1434E</c>.
    ///
    /// Falling comes first. Failing that, settled acid spreads sideways exactly as fire
    /// does, with the same direction memory in the state byte and the same burnout, so
    /// acid that cannot fall crawls along whatever is holding it up.
    /// </summary>
    private void AcidTerminal2B(int addr)
    {
        var below = addr + CaveGrid.RowStride;

        if (_grid.ValueAt(below) == RuntimeTile.Space)                      // 0x1434E
        {
            _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Space);
            _grid.WriteWord(below, 0xFF00 | RuntimeTile.AcidHead);
            Observe(Observation.Moved(addr, below, RuntimeTile.AcidHead));
            return;
        }

        if (_grid.ValueAt(below) == RuntimeTile.EnemyClockwise)             // 0x14365
        {
            Blast(addr - CaveGrid.CellStride, kind: 1);
            return;
        }

        // 0x1437D: the remembered direction, or one draw to pick a side. Bit 0 set
        // means left, the same sense fire uses at 0x144DF.
        var state = _grid.StateAt(addr);
        var goLeft = state == RuntimeTile.AcidGoingLeft
                     || (state != RuntimeTile.AcidGoingRight && (_random.NextByte() & 1) != 0);

        AcidSideways(addr, towardsLeft: goLeft);
    }

    /// <summary>
    /// The two sideways branches of settled acid, <c>0x143A0</c> and <c>0x143EF</c>.
    ///
    /// Mirrors of each other, and close to <see cref="FireSideways"/> but not the same
    /// shape. Acid attacks a creature beside it where fire converts treasure, acid has
    /// no collision test of its own, and the fallback to the opposite side tests only
    /// for space - it will not attack that way.
    /// </summary>
    private void AcidSideways(int addr, bool towardsLeft)
    {
        var preferred = addr + (towardsLeft ? -CaveGrid.CellStride : CaveGrid.CellStride);
        var other = addr + (towardsLeft ? CaveGrid.CellStride : -CaveGrid.CellStride);

        if (_grid.ValueAt(preferred) == RuntimeTile.EnemyClockwise)         // 0x143A0, 0x143EF
        {
            // Centred on the creature, by the same upper-left contract as everywhere
            // else: left anchors at addr - 0x104, right at addr - 0x100, which is what
            // stepping back from the neighbour gives.
            Blast(preferred - BlastCentreOffset, kind: 1);
            return;
        }

        if (_grid.ValueAt(preferred) == RuntimeTile.Space)
        {
            _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Space);
            WriteAcid(preferred, towardsLeft);
            return;
        }

        if (_grid.ValueAt(other) == RuntimeTile.Space)
        {
            _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Space);
            WriteAcid(other, !towardsLeft);
            return;
        }

        // 0x143DC and 0x1442B: boxed in, three draws in four burn it out.
        _grid.WriteWord(addr, (_random.NextByte() & 3) != 0
            ? (ushort)(0xFF00 | RuntimeTile.SpaceBlast3)
            : (ushort)(0xFF00 | RuntimeTile.AcidSettled));
    }

    /// <summary>
    /// Left is live, right is deferred, for the same scan-order reason as fire. Acid's
    /// deferred seed is <see cref="RuntimeTile.SeedAcidSettled"/>, not fire's.
    /// </summary>
    private void WriteAcid(int addr, bool goingLeft)
    {
        _grid[addr] = goingLeft ? RuntimeTile.AcidSettled : RuntimeTile.SeedAcidSettled;
        _grid[addr + 1] = goingLeft ? RuntimeTile.AcidGoingLeft : RuntimeTile.AcidGoingRight;
    }

    /// <summary>
    /// Fire's terminal, runtime <c>2F</c>, from <c>0x14473</c>.
    ///
    /// Rising is tried first. Failing that it goes sideways, preferring the direction
    /// it is already travelling, which is held in the cell's state byte as
    /// <see cref="RuntimeTile.FireGoingLeft"/> or <see cref="RuntimeTile.FireGoingRight"/>.
    /// Fire with neither - the first one out of a tap - picks a side from a draw.
    /// </summary>
    private void FireTerminal2F(int addr)
    {
        var above = addr - CaveGrid.RowStride;
        var up = _grid.ValueAt(above);

        if (up == RuntimeTile.Space)                                        // 0x14473
        {
            _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Space);
            _grid.WriteWord(above, 0xFF00 | RuntimeTile.FireHead);
            Observe(Observation.Moved(addr, above, RuntimeTile.FireHead));
            return;
        }

        if (IsAcid(up))                                                     // 0x1448A
        {
            Blast(addr - BlastCentreOffset, kind: 2);
            return;
        }

        if (up == RuntimeTile.Treasure)                                     // 0x144AD
        {
            TreasureStruckByFire(above);
            return;
        }

        // 0x144C3: the remembered direction, or a coin toss for fire that has none.
        var state = _grid.StateAt(addr);
        var goLeft = state == RuntimeTile.FireGoingLeft
                     || (state != RuntimeTile.FireGoingRight && (_random.NextByte() & 1) != 0);

        if (goLeft) FireSideways(addr, towardsLeft: true);
        else FireSideways(addr, towardsLeft: false);
    }

    /// <summary>
    /// The two sideways branches at <c>0x144E6</c> and <c>0x1454B</c>, which are exact
    /// mirrors of each other.
    ///
    /// Note which way each one writes. Moving left writes the live
    /// <see cref="RuntimeTile.FireHead"/>, because the scan has already passed that
    /// cell this step. Moving right writes the deferred
    /// <see cref="RuntimeTile.SeedFireHead"/>, because the scan has not reached it yet
    /// and the fire would otherwise act twice in one step.
    /// </summary>
    private void FireSideways(int addr, bool towardsLeft)
    {
        var preferred = addr + (towardsLeft ? -CaveGrid.CellStride : CaveGrid.CellStride);
        var other = addr + (towardsLeft ? CaveGrid.CellStride : -CaveGrid.CellStride);

        if (_grid.ValueAt(preferred) == RuntimeTile.Treasure)
        {
            TreasureStruckByFire(preferred);
            return;
        }

        if (_grid.ValueAt(preferred) == RuntimeTile.Space)
        {
            _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Space);
            WriteFire(preferred, towardsLeft);
            return;
        }

        if (_grid.ValueAt(other) == RuntimeTile.Space)
        {
            _grid.WriteWord(addr, 0xFF00 | RuntimeTile.Space);
            WriteFire(other, !towardsLeft);
            return;
        }

        if (IsAcid(_grid.ValueAt(preferred)))
        {
            Blast(addr - BlastCentreOffset, kind: 2);
            return;
        }

        // 0x14538: boxed in. Three draws in four burn it out, and the fourth restarts
        // the chain where it stands.
        _grid.WriteWord(addr, (_random.NextByte() & 3) != 0
            ? (ushort)(0xFF00 | RuntimeTile.SpaceBlast3)
            : (ushort)(0xFF00 | RuntimeTile.FireHead));
    }

    private void WriteFire(int addr, bool goingLeft)
    {
        // Left is live, right is deferred. See FireSideways.
        _grid[addr] = goingLeft ? RuntimeTile.FireHead : RuntimeTile.SeedFireHead;
        _grid[addr + 1] = goingLeft ? RuntimeTile.FireGoingLeft : RuntimeTile.FireGoingRight;
    }

    /// <summary>
    /// <c>0x1355E</c>. Treasure that fire touches becomes the clockwise creature, which
    /// is the family that yields treasure when it dies. So burning treasure does not
    /// destroy it: it puts it behind a creature you now have to kill.
    /// </summary>
    private void TreasureStruckByFire(int addr)
    {
        _grid[addr] = RuntimeTile.EnemyClockwise;
        _grid[addr + 1] = 0xF8;
        _grid.WriteWord(addr + CaveGrid.ShadowPlane, 0xFFFF);
    }

    /// <summary>
    /// The range fire tests against at <c>0x1448A</c>: <c>20</c> to <c>2B</c> inclusive.
    ///
    /// Note what it excludes. The acid head is <c>1F</c> and is NOT in this range, so
    /// fire meeting acid on the very first phase of its cycle does not explode. Only
    /// from the second phase on. That is the bound the executable tests and it is not
    /// rounded off here.
    /// </summary>
    private static bool IsAcid(byte value) =>
        value >= RuntimeTile.AcidRangeFirst && value <= RuntimeTile.AcidRangeLast;
}
