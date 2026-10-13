namespace Rockford.Engine;

public sealed partial class Simulation
{
    /// <summary>Points a treasure is worth while the quota is outstanding.</summary>
    public int ScoreBeforeQuota { get; set; } = 10;

    /// <summary>Points a treasure is worth once the quota has been met.</summary>
    public int ScoreAfterQuota { get; set; } = 10;

    /// <summary>Points awarded in this cave so far.</summary>
    public int Score { get; private set; }

    /// <summary>Points awarded since the host last collected them.</summary>
    private int _scoreDelta;

    /// <summary>
    /// Points awarded since the last call, and clears the total.
    ///
    /// A Simulation is one cave, and the score outlives caves, so the running
    /// total belongs to <see cref="GameSession"/>. Handing over a delta rather
    /// than the total is what stops a reload either losing the points or counting
    /// them twice.
    ///
    /// The host must drain this every step and before it resolves a death. The
    /// original checks its bonus-life threshold after every single award
    /// (<c>0x13C42</c>), so a life earned on the last gem of a cave exists before
    /// the death that cave might also be about to deal.
    /// </summary>
    public int TakeScoreDelta()
    {
        var earned = _scoreDelta;
        _scoreDelta = 0;
        return earned;
    }

    /// <summary>Awards points, keeping the per-cave total and the delta in step.</summary>
    private void AwardScore(int points)
    {
        Score += points;
        _scoreDelta += points;
    }

    /// <summary>
    /// Seconds left on the cave clock. Set from the constructor, and settable
    /// afterwards because the game changes it: walking into a clock bonus adds
    /// twenty at <c>0x1024C</c>, and finishing a cave runs it down to nothing.
    /// </summary>
    public int TimeRemaining { get; set; }

    /// <summary>The debug cheat, toggled while paused in the original. Off by default.
    /// <c>ds:0x16A4</c>.</summary>
    public bool Cheat { get; set; }

    /// <summary>The downward producer wakes once the player has touched or shot it.
    /// <c>ds:0x1678</c>. Its handler at <c>0x14445</c> is not ported yet.</summary>
    public bool ProducerDownArmed { get; private set; }

    /// <summary>The upward producer, the same but at <c>0x140AE</c>. <c>ds:0x166C</c>.</summary>
    public bool ProducerUpArmed { get; private set; }

    /// <summary>
    /// Hatches the first timed entity once it reaches 1200 interrupts, which is
    /// twenty seconds. <c>ds:0x16AA</c>.
    ///
    /// The interrupt increments it at <c>0x13380</c>, so a cave left alone hatches
    /// on its own - and walking into the thing or firing at it writes 1200 outright
    /// at <c>0x1013A</c>, which wakes it on the next scan. Touching it is the fast
    /// way to a worm, not a way to stop one.
    /// </summary>
    public int TimedEntityATimer { get; private set; }

    /// <summary>The second timed entity, the same. <c>ds:0x16AC</c>, <c>0x13384</c>.</summary>
    public int TimedEntityBTimer { get; private set; }

    /// <summary>Walking push succeeds when the low byte of a draw is under this.
    /// <c>cmp al,0x9E</c> at <c>0x1023D</c>, so 158 in 256.</summary>
    public const byte PushThreshold = 0x9E;

    /// <summary>
    /// What Rockford last did, which decides which sprite he is drawn as.
    /// <c>ds:0x2256</c>, read by the renderer at <c>0x12335</c>.
    ///
    /// It lives here rather than in a front end because the value is chosen by a
    /// draw from the game's own generator, and a draw made anywhere else would move
    /// the whole random stream.
    /// </summary>
    public int PlayerPose { get; private set; } = PoseIdleA;

    // The poses, each named for the instruction that writes it.
    public const int PoseLeft = 0x00;      // 0x1030A
    public const int PoseUp = 0x04;        // 0x10495
    public const int PoseRight = 0x08;     // 0x10487
    public const int PoseDown = 0x0C;      // 0x10067
    public const int PoseFireLeft = 0x10;  // 0x11F35
    public const int PoseFireRight = 0x14; // 0x11F47
    public const int PoseIdleA = 0x18;     // 0x11DB3
    public const int PoseIdleB = 0x20;     // 0x11DBB

    /// <summary>
    /// One player tick, from <c>0x11BF1</c>. The original runs this BEFORE the cell
    /// scan, which is why a player standing next to a creature dies where they stood
    /// rather than where they were about to move.
    ///
    /// The guards match <c>0xEDB2</c>, where the main loop skips this call entirely
    /// while the death timer is set - so a dying player makes no draw either.
    /// </summary>
    public void PlayerTick(PlayerAction input)
    {
        if (_playerCell < 0 || _playerDeathTimer > 0) return;

        // 0x11DAC, before the directions are read and before the adjacent creatures
        // are tested. It picks between the two idle poses on the low two bits, and
        // it happens on EVERY tick - walking and firing overwrite the pose
        // afterwards, but the draw has already been spent.
        //
        // That is the whole point of putting it here. Skipping it left the generator
        // one draw behind on every idle tick, and from seed zero the port sat at
        // 0x0000 where the original was at 0x1249 - shifting every later push,
        // amoeba and projectile decision in the cave.
        PlayerPose = (_random.NextByte() & 3) != 0 ? PoseIdleA : PoseIdleB;

        if (AdjacentCreatureKills(input)) return;

        if (input.Fire) Fire(input);
        else Walk(input);
    }

    /// <summary>
    /// Death by standing next to a creature, from <c>0x11E22</c>. All four neighbours
    /// are tested for the anticlockwise family first, then all four for the clockwise
    /// one, so if both are adjacent the space blast wins.
    /// </summary>
    private bool AdjacentCreatureKills(PlayerAction input)
    {
        var bx = _playerCell;

        // Holding up skips the right-hand test, in both blocks. There is no equivalent
        // for down, left or right. It is a quirk of the original, not a tidy rule.
        var checkRight = input.Vertical != PlayerAction.Up;

        if (Touches(bx, RuntimeTile.EnemyAnticlockwise, checkRight))
        {
            Blast(bx - BlastCentreOffset, kind: 2);
            return true;
        }

        if (Touches(bx, RuntimeTile.EnemyClockwise, checkRight))
        {
            Blast(bx - BlastCentreOffset, kind: 1);
            return true;
        }

        return false;
    }

    private bool Touches(int bx, byte enemy, bool checkRight) =>
        _grid.ValueAt(bx - CaveGrid.CellStride) == enemy
        || _grid.ValueAt(bx - CaveGrid.RowStride) == enemy
        || _grid.ValueAt(bx + CaveGrid.RowStride) == enemy
        || (checkRight && _grid.ValueAt(bx + CaveGrid.CellStride) == enemy);

    // ---- walking ----------------------------------------------------------------

    /// <summary>
    /// Horizontal is tried first and vertical is the fallback, from <c>0x11E95</c>.
    /// It is deliberately not symmetric: there is no vertical-first path, which is what
    /// gives diagonal input its feel.
    /// </summary>
    private void Walk(PlayerAction input)
    {
        // Read before, compared after. One site covers every direction and every
        // reason a step can fail, which is why it is here rather than inside each
        // branch of TryEachDirection.
        //
        // It is deliberately a POSITION-CHANGED test and not a walk-succeeded one,
        // the same test NoteMovement makes below, because the two must agree: a
        // player shoved sideways by their own push has moved, and a direction held
        // against a wall has not.
        var was = _playerCell;

        TryEachDirection(input);

        // Every direction's branch falls through to the tail at 0x11EF5 -
        // 0x11EBD and 0x11ED9 jump there explicitly rather than returning - so
        // this runs once per walk attempt, landed or not.
        NoteMovement();

        if (_playerCell != was && was >= 0 && _playerCell >= 0)
            Observe(Observation.Moved(was, _playerCell, RuntimeTile.Player));
    }

    /// <summary>
    /// The movement sound, from the walk routine's tail at <c>0x11EF5</c>.
    ///
    /// <code>
    /// 11ef5: cmp  BYTE PTR ds:0x166e,0xff
    /// 11efa: jne  0x11efd
    /// 11efc: ret
    /// 11efd: mov  ax,ds:0x16be
    /// 11f00: cmp  ax,WORD PTR ds:0x1df8
    /// 11f04: je   0x11f0e
    /// 11f06: or   BYTE PTR ds:0x2262,0x2
    /// 11f0b: mov  ds:0x1df8,ax
    /// </code>
    ///
    /// It is a position-changed test rather than a walk-succeeded one, which is
    /// not the same thing: a boulder pushed sideways moves the player and speaks,
    /// and a direction held against a wall does not. Firing never reaches here at
    /// all - <c>0x11F12</c> is a separate entry, and firing does not move anyone.
    /// </summary>
    private void NoteMovement()
    {
        // 0x11EFA. The player has stepped into the exit and the cave is over, so
        // the footstep is suppressed and the completion has the moment to itself.
        if (CaveComplete) return;

        if (_playerCell == LastSoundedCell) return;    // 0x11F04

        _soundFlags |= 0x02;                           // 0x11F06
        LastSoundedCell = _playerCell;                 // 0x11F0B
    }

    private void TryEachDirection(PlayerAction input)
    {
        // Sideways is written twice and the two writes mean different things. The
        // attempt starts by facing the way it is about to go, using the SAME pose
        // firing that way uses - 0x1019B writes 0x10 and 0x10318 writes 0x14 - and
        // only a step that actually lands replaces it with the walking pose at
        // 0x1030A and 0x10487.
        //
        // So a player who walks into a wall is drawn reaching for it, exactly as
        // if they had fired at it. That is where the two share a pose, and writing
        // the walking pose on entry loses it: Rockford would appear to walk
        // cheerfully on the spot against a boulder he cannot move.
        if (input.Horizontal == PlayerAction.Left)
        {
            PlayerPose = PoseFireLeft;                          // 0x1019B
            if (TryWalk(-CaveGrid.CellStride, input))
            {
                PlayerPose = PoseLeft;                          // 0x1030A
                return;
            }
        }

        if (input.Horizontal == PlayerAction.Right)
        {
            PlayerPose = PoseFireRight;                         // 0x10318
            if (TryWalk(CaveGrid.CellStride, input))
            {
                PlayerPose = PoseRight;                         // 0x10487
                return;
            }
        }

        // Vertical is written once, on entry, and never corrected. There is no
        // second write for either direction in the original - 0x10495 and 0x10067
        // are the only ones - so up and down look the same whether the step lands
        // or not.
        if (input.Vertical == PlayerAction.Up)
        {
            PlayerPose = PoseUp;                                // 0x10495
            TryWalk(-CaveGrid.RowStride, input);
        }
        else if (input.Vertical == PlayerAction.Down)
        {
            PlayerPose = PoseDown;                              // 0x10067
            TryWalk(CaveGrid.RowStride, input);
        }
    }

    private bool TryWalk(int delta, PlayerAction input)
    {
        var bx = _playerCell;
        var dest = bx + delta;

        // Each direction guards the edge before it reads any cell.
        var col = CaveGrid.ColumnOf(bx);
        var row = CaveGrid.RowOf(bx);
        if (delta == -CaveGrid.CellStride && col < 2) return false;
        if (delta == CaveGrid.CellStride && col > 37) return false;
        if (delta == -CaveGrid.RowStride && row < 2) return false;
        // The down guard compares the whole pointer, not the row. That is what the
        // original does at 0x10067 and it is not the same as "row < 21".
        if (delta == CaveGrid.RowStride && bx > 0x1400) return false;

        var ax = _grid.ReadWord(dest);
        var al = (byte)(ax & 0xFF);
        var horizontal = delta == -CaveGrid.CellStride || delta == CaveGrid.CellStride;
        var movingUp = delta == -CaveGrid.RowStride;
        var earlyCheat = delta == CaveGrid.CellStride || delta == -CaveGrid.RowStride;

        // The chain, in the original's order. Order is behaviour.
        if (al is RuntimeTile.Space or RuntimeTile.SpaceBlast3) return Commit(dest);
        if (al is >= 0x20 and <= 0x2F) return Commit(dest);

        if (ax is 0xFF40 or 0xFF42)
        {
            Collect();
            return Commit(dest);
        }

        if (ax == 0xFF01) return Commit(dest);

        if (ax is >= 0xFF70 and <= 0xFF73)
        {
            _soundFlags |= 0x04;
            TimeRemaining += 0x14;
            return Commit(dest);
        }

        if (al is >= 0x6C and <= 0x6F)
        {
            for (var i = 0; i < al - 0x6C; i++) Collect();
            return Commit(dest);
        }

        // Only a RESTING boulder can be pushed by walking, and only sideways.
        if (horizontal && ax == 0xFF44) return PushByWalking(bx, dest, delta);

        if (al is >= RuntimeTile.ExitOpen and <= RuntimeTile.ExitOpenLast)
        {
            CaveComplete = true;
            return Commit(dest);
        }

        // The cheat sits at a different point in the chain for each pair of directions.
        // Going right or up it comes before the blocking cases; going left or down it
        // comes after them. Only observable with the cheat on, but it is real.
        if (Cheat && earlyCheat)
        {
            _soundFlags |= 0x04;
            return Commit(dest);
        }

        if (al == RuntimeTile.ProducerDown) { ProducerDownArmed = true; return false; }
        if (al == RuntimeTile.ProducerUp) { ProducerUpArmed = true; return false; }
        if (al is >= 0x78 and <= 0x7B) { TimedEntityATimer = 0x4B0; return false; }
        if (al is >= 0x7C and <= 0x7F) { TimedEntityBTimer = 0x4B0; return false; }

        // Walking UP into something already falling kills the player. No other
        // direction has this test.
        if (movingUp && al is RuntimeTile.BoulderFalling or RuntimeTile.TreasureFalling)
        {
            Blast(bx - BlastCentreOffset, kind: 1);
            return false;
        }

        if (Cheat)
        {
            _soundFlags |= 0x04;
            return Commit(dest);
        }

        return false;
    }

    /// <summary>The sideways push, from <c>0x10223</c>.</summary>
    private bool PushByWalking(int bx, int dest, int delta)
    {
        // The cheat sits between the boulder test and the draw, so with it on the
        // random is never rolled and the boulder is simply overwritten.
        if (Cheat)
        {
            _soundFlags |= 0x04;
            return Commit(dest);
        }

        // A push is not certain. Roughly three times in five.
        if (_random.NextByte() >= PushThreshold) return false;

        var beyond = dest + delta;
        if (_grid.ReadWord(beyond) != 0xFF00) return false;

        _grid.WriteWord(beyond, 0xFF44);
        _grid.WriteWord(beyond + CaveGrid.ShadowPlane, 0xFFFF);

        // The boulder moved from the cell the player is stepping into, to the one
        // past it. Reported before Commit, so it reads in the order it happened:
        // the rock is shoved and the player follows into the space it left.
        Observe(Observation.Moved(dest, beyond, RuntimeTile.Boulder));

        return Commit(dest);
    }

    private bool Commit(int dest)
    {
        _grid.WriteWord(_playerCell, 0xFF00);
        _grid.WriteWord(_playerCell + CaveGrid.ShadowPlane, 0xFFFF);
        _grid.WriteWord(dest, 0x1110);
        _grid.WriteWord(dest + CaveGrid.ShadowPlane, 0x1111);
        _playerCell = dest;
        return true;
    }

    // ---- fire -------------------------------------------------------------------

    /// <summary>
    /// The ranged action, from <c>0x11F12</c>. The player never moves on this path.
    /// Horizontal wins outright; unlike walking there is no vertical fallback.
    /// </summary>
    private void Fire(PlayerAction input)
    {
        if (!input.AnyDirection) return;

        _soundFlags |= 0x08;

        var bx = _playerCell;
        if (input.Horizontal == PlayerAction.Left)
        {
            bx -= CaveGrid.CellStride;
            PlayerPose = PoseFireLeft;          // 0x11F35
        }
        else if (input.Horizontal == PlayerAction.Right)
        {
            bx += CaveGrid.CellStride;
            PlayerPose = PoseFireRight;         // 0x11F47
        }
        else if (input.Vertical == PlayerAction.Up) bx -= CaveGrid.RowStride;
        else if (input.Vertical == PlayerAction.Down) bx += CaveGrid.RowStride;

        if (bx == _playerCell) return;

        // The cell being fired AT. This is the distinction SoundEvent cannot make:
        // the original serves firing and an explosion from the same request, on
        // purpose, so a front end reading only the sound would shake the screen on
        // every shot.
        Observe(Observation.Fired(bx));

        var ax = _grid.ReadWord(bx);
        var al = (byte)(ax & 0xFF);

        if (ax is 0xFF40 or 0xFF42)
        {
            Clear(bx);
            Collect();
            return;
        }

        // Firing at empty space still plants the puff, which is why a shot into nothing
        // is not free: the cell takes three more scans to become walkable.
        if (ax == 0xFF01 || al == RuntimeTile.Space || al is >= 0x20 and <= 0x2F)
        {
            _grid.WriteWord(bx, 0xFF00 | RuntimeTile.DigEffect);
            _grid.WriteWord(bx + CaveGrid.ShadowPlane, 0xFFFF);
            return;
        }

        // Fire pushes a FALLING boulder too, which walking cannot, and never rolls for
        // it. But it is horizontal only, so firing down at a boulder does nothing.
        if (ax is 0xFF44 or 0xFF46)
        {
            FirePush(bx, input);
            return;
        }

        // These two test the whole word where the walk chain tests the low byte, so
        // only the base animation frame responds to a shot.
        if (ax == 0xFF08) { ProducerDownArmed = true; return; }
        if (ax == 0xFF0C) { ProducerUpArmed = true; return; }

        if (al is >= 0x78 and <= 0x7B) { TimedEntityATimer = 0x4B0; return; }
        if (al is >= 0x7C and <= 0x7F) { TimedEntityBTimer = 0x4B0; return; }

        if (al is >= 0x70 and <= 0x73)
        {
            _soundFlags |= 0x04;
            TimeRemaining += 0x14;
            Clear(bx);
            return;
        }

        if (al is >= 0x6C and <= 0x6F)
        {
            for (var i = 0; i < al - 0x6C; i++) Collect();
            Clear(bx);
        }
    }

    private void FirePush(int bx, PlayerAction input)
    {
        var delta = input.Horizontal switch
        {
            PlayerAction.Left => -CaveGrid.CellStride,
            PlayerAction.Right => CaveGrid.CellStride,
            _ => 0,
        };

        if (delta == 0) return;
        if (_grid.ReadWord(bx + delta) != 0xFF00) return;

        // The boulder's old cell becomes the dig puff, not space.
        _grid.WriteWord(bx, 0xFF00 | RuntimeTile.DigEffect);
        _grid.WriteWord(bx + CaveGrid.ShadowPlane, 0xFFFF);
        _grid.WriteWord(bx + delta, 0xFF44);
        _grid.WriteWord(bx + delta + CaveGrid.ShadowPlane, 0xFFFF);

        Observe(Observation.Moved(bx, bx + delta, RuntimeTile.Boulder));
    }

    private void Clear(int bx)
    {
        _grid.WriteWord(bx, 0xFF00);
        _grid.WriteWord(bx + CaveGrid.ShadowPlane, 0xFFFF);
    }

    /// <summary>
    /// One treasure, from <c>0x116A3</c>. The score is awarded BEFORE the quota is
    /// decremented, so the treasure that completes a quota still pays the lower rate
    /// and the first one after it pays the higher.
    /// </summary>
    private void Collect()
    {
        AwardScore(TreasureQuota > 0 ? ScoreBeforeQuota : ScoreAfterQuota);
        _soundFlags |= 0x04;
        TreasureQuota--;
        if (TreasureQuota < 0) TreasureQuota = 0;
    }
}
