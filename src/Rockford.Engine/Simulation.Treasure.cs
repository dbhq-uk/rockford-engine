namespace Rockford.Engine;

public sealed partial class Simulation
{
    /// <summary>Resting treasure, runtime <c>0x40</c>, handler <c>0x1414A</c>.</summary>
    private void RestingTreasure(int bx)
    {
        var below = _grid.ValueAt(bx + CaveGrid.RowStride);

        // 0x14150: start falling without moving, exactly as the boulder does.
        if (IsFallable(below))
        {
            _grid.WriteWord(bx, 0xFF00 | RuntimeTile.TreasureFalling);
            return;
        }

        // 0x14162: roll off a rounded shoulder, left before right.
        if (IsRoundedShoulder(below))
        {
            if (_grid.ValueAt(bx - CaveGrid.CellStride) == RuntimeTile.Space
                && _grid.ValueAt(bx + CaveGrid.RowStride - CaveGrid.CellStride) == RuntimeTile.Space)
            {
                _grid.WriteWord(bx - CaveGrid.CellStride, 0xFF00 | RuntimeTile.TreasureFalling);
                _grid.WriteWord(bx, 0xFF00 | RuntimeTile.Space);
                Observe(Observation.Moved(bx, bx - CaveGrid.CellStride, RuntimeTile.Treasure));
                return;
            }

            if (_grid.ValueAt(bx + CaveGrid.CellStride) == RuntimeTile.Space
                && _grid.ValueAt(bx + CaveGrid.RowStride + CaveGrid.CellStride) == RuntimeTile.Space)
            {
                _grid.WriteWord(bx + CaveGrid.CellStride, 0xFF00 | RuntimeTile.TreasureFallingDeferred);
                _grid.WriteWord(bx, 0xFF00 | RuntimeTile.Space);
                Observe(Observation.Moved(bx, bx + CaveGrid.CellStride, RuntimeTile.Treasure));
            }

            return;
        }

        // 0x141AA: the timed wall transmutes what falls through it. A boulder becomes
        // treasure and treasure becomes a boulder.
        if (below is >= RuntimeTile.TimedWall and <= RuntimeTile.TimedWallLast)
        {
            if (!TimedWallLatched)
            {
                TimedWallLatched = true;
                TimedWallStart = _interruptTicks;
            }

            if (!TimedWallConverting())
                return;

            // The input is consumed before the output is checked for room, so a
            // blocked wall destroys what enters it.
            _grid.WriteWord(bx, 0xFF00 | RuntimeTile.SpaceBlast3);
            _grid.WriteWord(bx + CaveGrid.ShadowPlane, 0xFFFF);

            if (_grid.ValueAt(bx + 2 * CaveGrid.RowStride) == RuntimeTile.Space)
            {
                _grid.WriteWord(bx + 2 * CaveGrid.RowStride, 0xFF00 | RuntimeTile.BoulderFallingDeferred);
                _grid.WriteWord(bx + 2 * CaveGrid.RowStride + CaveGrid.ShadowPlane, 0xFFFF);
            }

            return;
        }

        // 0x141F6: the pass-through wall keeps the type it is given.
        if (below == RuntimeTile.PassThroughWall)
        {
            // The draw happens BEFORE the room check, so a blocked cell still burns a
            // random number. Keeping that order is what keeps a replay deterministic.
            if (_random.Next() >= 0xB0)
                return;

            if (_grid.ValueAt(bx + 2 * CaveGrid.RowStride) != RuntimeTile.Space)
                return;

            _grid.WriteWord(bx, 0xFF00 | RuntimeTile.SpaceBlast3);
            _grid.WriteWord(bx + CaveGrid.ShadowPlane, 0xFFFF);
            _grid.WriteWord(bx + 2 * CaveGrid.RowStride, 0xFF00 | RuntimeTile.TreasureFallingDeferred);
            _grid.WriteWord(bx + 2 * CaveGrid.RowStride + CaveGrid.ShadowPlane, 0xFFFF);
        }
    }

    /// <summary>Falling treasure, runtime <c>0x42</c>, handler <c>0x14223</c>.</summary>
    private void FallingTreasure(int bx)
    {
        var below = _grid.ValueAt(bx + CaveGrid.RowStride);

        if (IsFallable(below))
        {
            _grid.WriteWord(bx, 0xFF00 | RuntimeTile.Space);
            _grid.WriteWord(bx + CaveGrid.RowStride, 0xFF00 | RuntimeTile.TreasureFallingDeferred);
            Observe(Observation.Moved(bx, bx + CaveGrid.RowStride, RuntimeTile.Treasure));
            return;
        }

        if (below == RuntimeTile.EnemyClockwise)
        {
            Blast(bx - CaveGrid.CellStride, kind: 1);
            return;
        }

        if (below == RuntimeTile.EnemyAnticlockwise)
        {
            Blast(bx - CaveGrid.CellStride, kind: 2);
            return;
        }

        if (below == RuntimeTile.Player)
        {
            // Kind 1, where a falling BOULDER on the player is kind 2. The asymmetry is
            // in the bytes at 0x14271 against 0x1401E, and it is not a misreading: a
            // falling gem leaves treasure behind where a boulder leaves space.
            Blast(bx - CaveGrid.CellStride, kind: 1);
            return;
        }

        if (IsRoundedShoulder(below))
        {
            _soundFlags |= 0x01;    // 0x14291, before either roll is attempted

            if (_grid.ValueAt(bx - CaveGrid.CellStride) == RuntimeTile.Space
                && IsRollableDiagonal(_grid.ValueAt(bx + CaveGrid.RowStride - CaveGrid.CellStride)))
            {
                _grid.WriteWord(bx - CaveGrid.CellStride, 0xFF00 | RuntimeTile.TreasureFalling);
                _grid.WriteWord(bx, 0xFF00 | RuntimeTile.Space);
                Observe(Observation.Moved(bx, bx - CaveGrid.CellStride, RuntimeTile.Treasure));
                return;
            }

            if (_grid.ValueAt(bx + CaveGrid.CellStride) == RuntimeTile.Space
                && IsRollableDiagonal(_grid.ValueAt(bx + CaveGrid.RowStride + CaveGrid.CellStride)))
            {
                _grid.WriteWord(bx + CaveGrid.CellStride, 0xFF00 | RuntimeTile.TreasureFallingDeferred);
                _grid.WriteWord(bx, 0xFF00 | RuntimeTile.Space);
                Observe(Observation.Moved(bx, bx + CaveGrid.CellStride, RuntimeTile.Treasure));
                return;
            }

            _grid.WriteWord(bx, 0xFF00 | RuntimeTile.Treasure);
            Observe(Observation.Landed(bx, RuntimeTile.Treasure));
            return;
        }

        // Anything else, including the timed wall and the pass-through wall. A falling
        // object does NOT convert: it lands on the wall, and the RESTING handler does
        // the conversion on the next scan. Implementing it in one pass fires the wall a
        // frame early.
        _grid.WriteWord(bx, 0xFF00 | RuntimeTile.Treasure);
        _soundFlags |= 0x01;
        Observe(Observation.Landed(bx, RuntimeTile.Treasure));
    }
}
