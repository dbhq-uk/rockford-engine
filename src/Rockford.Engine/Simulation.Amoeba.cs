namespace Rockford.Engine;

public sealed partial class Simulation
{
    /// <summary>Grows into a neighbour when the low byte of a draw is under this.
    /// From <c>cmp al,0x02</c> at <c>0x1175A</c>, so 2 in 256 per eligible neighbour
    /// per scan, not one in four.</summary>
    public const int AmoebaGrowthThreshold = 2;

    /// <summary>Colony size at which the whole amoeba turns to falling boulders.
    /// Per world, from the table at <c>0x2274</c>.</summary>
    public int AmoebaCap { get; set; } = 160;

    /// <summary>True when the cave was loaded with at least one amoeba. Without this
    /// guard a cave with no amoeba would satisfy the enclosed test on the first scan.
    /// <c>ds:0x166A</c>.</summary>
    public bool CaveHasAmoeba { get; set; }

    private int _amoebaCount;           // ds:0x1604
    private bool _amoebaEnclosed;       // ds:0x16A0, true until something disproves it
    private ushort _amoebaGrowWord;     // ds:0x1606

    /// <summary>Amoeba, runtime <c>0x48</c> to <c>0x4B</c>, handler <c>0x13E50</c>.</summary>
    private void Amoeba(int si, byte value)
    {
        // 0x13E50: animate first, and the frame it grows into neighbours is the NEW
        // one. Only the value byte is written, so the facing byte survives.
        var v = (byte)(value + 1);
        if (v > RuntimeTile.AmoebaLast) v = RuntimeTile.Amoeba;
        _grid[si] = v;

        // 0x13E5C: the growth routine clobbers AX, so the value to plant is carried in
        // a global rather than a register. That is the only reason this variable exists.
        _amoebaGrowWord = (ushort)(0xFF00 | v);

        GrowFourWays(si);

        // 0x13E62: counted AFTER growing, so a cell planted earlier in this same scan
        // is counted too if the scan reaches it.
        _amoebaCount++;
        if (_amoebaCount >= AmoebaCap)
        {
            // 0x13E6F: too big, and the whole colony drops as boulders. This happens
            // mid-scan, so amoeba cells further along are already boulders when the
            // scan gets to them and fall in the same pass.
            ConvertWholeColony(toTreasureBlast: false);
            CaveHasAmoeba = false;
        }
    }

    /// <summary>Up, left, right, down, always all four, from <c>0x11719</c>.</summary>
    private void GrowFourWays(int si)
    {
        TryGrow(si - CaveGrid.RowStride);
        TryGrow(si - CaveGrid.CellStride);
        TryGrow(si + CaveGrid.CellStride);
        TryGrow(si + CaveGrid.RowStride);
    }

    /// <summary>One neighbour, from <c>0x11746</c>.</summary>
    private void TryGrow(int bx)
    {
        var al = _grid.ValueAt(bx);

        if (al is RuntimeTile.Space or RuntimeTile.Dirt)
        {
            // 0x11752: cleared BEFORE the draw. Failing the roll does not make the
            // amoeba enclosed - having anywhere to go at all is what counts.
            _amoebaEnclosed = false;

            // Only an eligible neighbour draws. A cell with no space or dirt around it
            // consumes nothing, so the random stream advances by exactly the number of
            // open neighbours. Getting that count wrong desynchronises everything later.
            if (_random.NextByte() < AmoebaGrowthThreshold)
            {
                _grid.WriteWord(bx, _amoebaGrowWord);
                _grid.WriteWord(bx + CaveGrid.ShadowPlane, 0xFFFF);
            }

            return;
        }

        // Touching a creature both keeps the amoeba free and detonates the creature,
        // every scan. 0x1176F and 0x11784.
        if (al == RuntimeTile.EnemyClockwise)
        {
            _amoebaEnclosed = false;
            Blast(bx - BlastCentreOffset, kind: 1);
            return;
        }

        if (al == RuntimeTile.EnemyAnticlockwise)
        {
            _amoebaEnclosed = false;
            Blast(bx - BlastCentreOffset, kind: 2);
        }
    }

    /// <summary>
    /// Rewrites every amoeba cell in the cave, from <c>0xEF1E</c> and <c>0xEEED</c>.
    /// The two routines are the same loop with a different per-cell writer, and the
    /// cell test is a WORD compare so the facing byte has to be <c>0xFF</c>.
    /// </summary>
    private void ConvertWholeColony(bool toTreasureBlast)
    {
        for (var y = 0; y < CaveGrid.Height; y++)
        for (var x = 0; x < CaveGrid.Width; x++)
        {
            var p = CaveGrid.AddressOf(x, y);
            var w = _grid.ReadWord(p);
            if (w is < 0xFF48 or > 0xFF4B) continue;

            if (toTreasureBlast)
            {
                BlastCellTreasure(p);
            }
            else
            {
                // 0x12ACC. Steel is tested and cannot match here, but the test is real.
                _grid.WriteWord(p, 0xFF00 | RuntimeTile.BoulderFalling);
                _grid.WriteWord(p + CaveGrid.ShadowPlane, 0xFFFF);
            }
        }
    }
}
