namespace Rockford.Engine;

public sealed partial class Simulation
{
    /// <summary>
    /// Values a falling object comes to rest on, and therefore tries to roll off.
    /// From the run of compares at <c>0x13F0F</c>: boulder, falling boulder, ordinary
    /// wall, falling treasure, treasure. Note steel and dirt are absent, so a boulder
    /// on either of those simply stops.
    /// </summary>
    private static bool IsRoundedShoulder(byte v) => v
        is RuntimeTile.Boulder
        or RuntimeTile.BoulderFalling
        or RuntimeTile.Wall
        or RuntimeTile.TreasureFalling
        or RuntimeTile.Treasure;

    /// <summary>
    /// Space, for the purpose of falling into. <c>0x1E</c> counts because it is the
    /// last phase of a space explosion and is about to become space anyway.
    /// </summary>
    private static bool IsFallable(byte v) => v is RuntimeTile.Space or RuntimeTile.SpaceBlast3;

    /// <summary>Resting boulder, runtime <c>0x44</c>, handler <c>0x13EF7</c>.</summary>
    private void RestingBoulder(int bx)
    {
        var below = _grid.ValueAt(bx + CaveGrid.RowStride);

        // 0x13EFD: nothing underneath, so start falling WITHOUT moving. The falling
        // handler does the displacement on a later visit. This one-step delay is why
        // a boulder does not drop the instant the ground is taken from under it.
        if (IsFallable(below))
        {
            _grid.WriteWord(bx, 0xFF00 | RuntimeTile.BoulderFalling);
            return;
        }

        // 0x13F0F: landed on something rounded, so try to roll off it.
        if (IsRoundedShoulder(below))
        {
            // Left is tried first, at 0x13F26.
            if (_grid.ValueAt(bx - CaveGrid.CellStride) == RuntimeTile.Space
                && _grid.ValueAt(bx + CaveGrid.RowStride - CaveGrid.CellStride) == RuntimeTile.Space)
            {
                // Live faller: the scan has already passed this cell, so it cannot act again.
                _grid.WriteWord(bx - CaveGrid.CellStride, 0xFF00 | RuntimeTile.BoulderFalling);
                _grid.WriteWord(bx, 0xFF00 | RuntimeTile.Space);
                Observe(Observation.Moved(bx, bx - CaveGrid.CellStride, RuntimeTile.Boulder));
                return;
            }

            // Then right, at 0x13F3E.
            if (_grid.ValueAt(bx + CaveGrid.CellStride) == RuntimeTile.Space
                && _grid.ValueAt(bx + CaveGrid.RowStride + CaveGrid.CellStride) == RuntimeTile.Space)
            {
                // Deferred: the scan has NOT reached this cell yet, so it must not act
                // again this step.
                _grid.WriteWord(bx + CaveGrid.CellStride, 0xFF00 | RuntimeTile.BoulderFallingDeferred);
                _grid.WriteWord(bx, 0xFF00 | RuntimeTile.Space);
                Observe(Observation.Moved(bx, bx + CaveGrid.CellStride, RuntimeTile.Boulder));
            }

            return;
        }

        // 0x13F57: the timed converting wall.
        if (below is >= RuntimeTile.TimedWall and <= RuntimeTile.TimedWallLast)
        {
            // 0x13F65: the first object to touch any such wall starts the clock for
            // every one of them. It is a single global latch, not per wall.
            if (!TimedWallLatched)
            {
                TimedWallLatched = true;
                TimedWallStart = _interruptTicks;
            }

            // Past the window the wall does nothing at all and the boulder just
            // sits there. The elapsed arithmetic is sixteen bits and signed -
            // see TimedWallElapsed, which is why it is not written out here.
            if (!TimedWallConverting()) return;

            _grid.WriteWord(bx, 0xFF00 | RuntimeTile.SpaceBlast3);
            _grid.WriteWord(bx + CaveGrid.ShadowPlane, 0xFFFF);

            // 0x13F8D: the output only appears if there is room two rows down. If there
            // is not, the boulder is consumed for nothing.
            if (_grid.ValueAt(bx + 2 * CaveGrid.RowStride) == RuntimeTile.Space)
            {
                _grid.WriteWord(bx + 2 * CaveGrid.RowStride, 0xFF00 | RuntimeTile.TreasureFallingDeferred);
                _grid.WriteWord(bx + 2 * CaveGrid.RowStride + CaveGrid.ShadowPlane, 0xFFFF);
            }

            return;
        }

        // 0x13FA3: the pass-through wall lets a boulder through unchanged, rarely.
        if (below == RuntimeTile.PassThroughWall)
        {
            // 0x13FAD compares the FULL AX against 0xB0, not AL, so this is 176 in
            // 65536 per visit rather than 176 in 256. It is a much rarer event than it
            // looks.
            if (_random.Next() >= 0xB0)
                return;

            if (_grid.ValueAt(bx + 2 * CaveGrid.RowStride) != RuntimeTile.Space)
                return;

            _grid.WriteWord(bx, 0xFF00 | RuntimeTile.SpaceBlast3);
            _grid.WriteWord(bx + CaveGrid.ShadowPlane, 0xFFFF);
            _grid.WriteWord(bx + 2 * CaveGrid.RowStride, 0xFF00 | RuntimeTile.BoulderFallingDeferred);
            _grid.WriteWord(bx + 2 * CaveGrid.RowStride + CaveGrid.ShadowPlane, 0xFFFF);
        }
    }

    /// <summary>Falling boulder, runtime <c>0x46</c>, handler <c>0x13FD0</c>.</summary>
    private void FallingBoulder(int bx)
    {
        var below = _grid.ValueAt(bx + CaveGrid.RowStride);

        // 0x13FD6: keep falling. The cell below becomes a DEFERRED faller because the
        // scan reaches it later in this same step and it must not fall twice.
        if (IsFallable(below))
        {
            _grid.WriteWord(bx, 0xFF00 | RuntimeTile.Space);
            _grid.WriteWord(bx + CaveGrid.RowStride, 0xFF00 | RuntimeTile.BoulderFallingDeferred);
            Observe(Observation.Moved(bx, bx + CaveGrid.RowStride, RuntimeTile.Boulder));
            return;
        }

        // 0x13FEE onward. The blast's upper-left corner is one cell to the LEFT of the
        // boulder, which puts the 3 by 3 block's centre on the victim below it.
        if (below == RuntimeTile.EnemyClockwise)
        {
            Blast(bx - CaveGrid.CellStride, kind: 1);   // leaves treasure
            return;
        }

        if (below == RuntimeTile.EnemyAnticlockwise)
        {
            Blast(bx - CaveGrid.CellStride, kind: 2);   // leaves space
            return;
        }

        if (below == RuntimeTile.Player)
        {
            Blast(bx - CaveGrid.CellStride, kind: 2);
            return;
        }

        // 0x14027: landed on something rounded.
        if (IsRoundedShoulder(below))
        {
            _soundFlags |= 0x10;    // 0x1403E, set before the rolls are even tried

            // A falling object may roll over a diagonal cell holding an ENEMY, which a
            // resting one may not. 0x14050 and 0x14057 are the extra compares.
            if (_grid.ValueAt(bx - CaveGrid.CellStride) == RuntimeTile.Space
                && IsRollableDiagonal(_grid.ValueAt(bx + CaveGrid.RowStride - CaveGrid.CellStride)))
            {
                _grid.WriteWord(bx - CaveGrid.CellStride, 0xFF00 | RuntimeTile.BoulderFalling);
                _grid.WriteWord(bx, 0xFF00 | RuntimeTile.Space);
                Observe(Observation.Moved(bx, bx - CaveGrid.CellStride, RuntimeTile.Boulder));
                return;
            }

            if (_grid.ValueAt(bx + CaveGrid.CellStride) == RuntimeTile.Space
                && IsRollableDiagonal(_grid.ValueAt(bx + CaveGrid.RowStride + CaveGrid.CellStride)))
            {
                _grid.WriteWord(bx + CaveGrid.CellStride, 0xFF00 | RuntimeTile.BoulderFallingDeferred);
                _grid.WriteWord(bx, 0xFF00 | RuntimeTile.Space);
                Observe(Observation.Moved(bx, bx + CaveGrid.CellStride, RuntimeTile.Boulder));
                return;
            }

            // 0x14093: nowhere to roll, so come to rest.
            _grid.WriteWord(bx, 0xFF00 | RuntimeTile.Boulder);
            Observe(Observation.Landed(bx, RuntimeTile.Boulder));
            return;
        }

        // 0x1409A: landed on anything else - dirt, steel, the exit - and stops.
        _grid.WriteWord(bx, 0xFF00 | RuntimeTile.Boulder);
        _soundFlags |= 0x10;
        Observe(Observation.Landed(bx, RuntimeTile.Boulder));
    }

    /// <summary>
    /// What a FALLING object will roll over diagonally. Space, or either enemy. A
    /// resting object requires plain space, which is the difference between
    /// <c>0x13F2C</c> and <c>0x14049</c>.
    /// </summary>
    private static bool IsRollableDiagonal(byte v) =>
        v == RuntimeTile.Space || RuntimeTile.IsEnemy(v);
}
