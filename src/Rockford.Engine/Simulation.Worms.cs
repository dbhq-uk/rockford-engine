namespace Rockford.Engine;

public sealed partial class Simulation
{
    /// <summary>
    /// The two worms - the last entities the port was missing.
    ///
    /// A worm is not a cell the scan moves about. It is a routine called once a
    /// step from the main loop, at <c>0xEDBC</c> and <c>0xEDBF</c>, holding six
    /// globals of its own; the cells the scan sees are only the trail it has
    /// already laid. That is why nothing in the handler table ever moved one and
    /// why the port's scan-shaped model had no place to put it.
    ///
    /// <c>0x1194B</c> and <c>0x11A68</c> are the same routine twice over, to the
    /// instruction. They differ in four things: which globals they use, the seed
    /// they leave behind (<c>0xF3</c> against <c>0xF4</c>), the body value they
    /// write (<c>0x4F</c> against <c>0x5F</c>), and what they do to whatever is in
    /// front of them. So this is written once and given the state to work on.
    /// </summary>
    private sealed class WormState
    {
        /// <summary>
        /// Which way it is heading. <c>ds:0x1610</c> / <c>ds:0x1618</c>. Not cleared
        /// by the loader, so it carries from cave to cave.
        ///
        /// Nothing masks it on the way OUT. <c>0x11969</c> shifts the whole word and
        /// indexes the delta table with it, so a heading of 4 would read the zero
        /// word that follows the table and the worm would try to move nowhere. The
        /// two turns mask with 3 and so does the draw at <c>0x11A0B</c>, so a
        /// running game never produces one - which is why the setter refuses it
        /// rather than the read papering over it.
        /// </summary>
        public int Direction;

        /// <summary>Body cells, capped at <see cref="MaxLength"/>. Zero means it
        /// has not hatched, and the routine returns on it. <c>ds:0x1612</c> /
        /// <c>ds:0x161A</c>, cleared per cave at <c>0xFEBA</c> / <c>0xFEC0</c>.</summary>
        public int Length;

        /// <summary>The newest body cell. <c>ds:0x1614</c> / <c>ds:0x161C</c>.
        /// Cleared at <c>0x10878</c>, then set by the loader at <c>0x109FB</c> /
        /// <c>0x10A37</c> to the cell the authored egg was placed in.</summary>
        public int Head;

        /// <summary>The oldest body cell, and the one the scan pins at the last
        /// segment value. <c>ds:0x1616</c> / <c>ds:0x161E</c>. Zero until the worm
        /// is two cells long, which is what <see cref="Stuck"/> tests.</summary>
        public int Tail;

        /// <summary>Set on the step the worm turned round. <c>ds:0x1698</c> /
        /// <c>ds:0x169A</c>.</summary>
        public bool Reversed;

        /// <summary>Set on a step it could not move at all. <c>ds:0x169C</c> /
        /// <c>ds:0x169E</c>.</summary>
        public bool Blocked;

        /// <summary>The seed left on the cell that becomes the tail. <c>0xF3</c>
        /// at <c>0x119B6</c>, <c>0xF4</c> at <c>0x11AD3</c>.</summary>
        public required byte Seed { get; init; }

        /// <summary>The seed written on the new head. <c>0x4F</c> at <c>0x119E1</c>,
        /// <c>0x5F</c> at <c>0x11AFE</c>.</summary>
        public required byte Body { get; init; }

        /// <summary>True for the first worm, which is the one that draws from the
        /// generator when it meets a boulder.</summary>
        public required bool IsFirst { get; init; }
    }

    // ds:0x1610-0x1616 and ds:0x1618-0x161E.
    private readonly WormState _wormA = new() { Seed = RuntimeTile.SeedA7, Body = 0x4F, IsFirst = true };
    private readonly WormState _wormB = new() { Seed = RuntimeTile.SeedB7, Body = 0x5F, IsFirst = false };

    /// <summary>Body cells a worm is allowed. <c>0x119BF</c> compares against 8 and
    /// <c>0x119CE</c> writes it back, and the eight segment values <c>A0-A7</c> are
    /// exactly that many.</summary>
    public const int MaxLength = 8;

    /// <summary>
    /// The delta table at <c>ds:0x1656</c> - the same one the enemies index, which
    /// is what fixes the direction numbers.
    /// </summary>
    private static readonly int[] Delta =
    [
        -CaveGrid.CellStride,   // DirLeft
        -CaveGrid.RowStride,    // DirUp
        CaveGrid.CellStride,    // DirRight
        CaveGrid.RowStride,     // DirDown
    ];

    /// <summary>
    /// How many body cells the first worm has. <c>ds:0x1612</c>.
    ///
    /// Not settable, unlike the head and the direction beside it. This one the
    /// loader really does clear per cave, at <c>0xFEBA</c>, so nothing outside the
    /// cave has an opinion about it: hatching at <c>0x13EE4</c> writes the one, and
    /// the movement routine owns it after that.
    /// </summary>
    public int WormALength { get => _wormA.Length; private set => _wormA.Length = value; }

    /// <summary>The second worm's length. <c>ds:0x161A</c>, cleared at <c>0xFEC0</c>.</summary>
    public int WormBLength { get => _wormB.Length; private set => _wormB.Length = value; }

    /// <summary>
    /// The first worm's newest body cell. <c>ds:0x1614</c>.
    ///
    /// Settable because the loader is what seeds it: <c>0x109FB</c> records the cell
    /// of the authored <c>0x70</c>, and hatching at <c>0x13EE4</c> sets only the
    /// length. A host that builds the Simulation itself has to pass
    /// <see cref="CaveLoader.TimedEntityACell"/> in or the worm hatches with its
    /// head still at <c>0x10878</c>'s zero and grows out of the top-left corner.
    /// </summary>
    public int WormAHead { get => _wormA.Head; set => _wormA.Head = value; }

    /// <summary>The second worm's head. <c>ds:0x161C</c>, seeded at <c>0x10A37</c>.</summary>
    public int WormBHead { get => _wormB.Head; set => _wormB.Head = value; }

    /// <summary>The first worm's oldest body cell. <c>ds:0x1616</c>.</summary>
    public int WormATail => _wormA.Tail;

    /// <summary>The second worm's oldest body cell. <c>ds:0x161E</c>.</summary>
    public int WormBTail => _wormB.Tail;

    /// <summary>Which way the first worm is heading. <c>ds:0x1610</c>, which the
    /// loader leaves alone - see <see cref="CarryGlobalsFrom"/>.</summary>
    public int WormADirection
    {
        get => _wormA.Direction;
        set => _wormA.Direction = Heading(value);
    }

    /// <summary>Which way the second worm is heading. <c>ds:0x1618</c>.</summary>
    public int WormBDirection
    {
        get => _wormB.Direction;
        set => _wormB.Direction = Heading(value);
    }

    /// <summary>
    /// Refuses a heading the game cannot produce.
    ///
    /// The read at <c>0x11969</c> does not mask, so anything outside 0 to 3 indexes
    /// past the delta table at <c>ds:0x1656</c> and picks up the zero word behind
    /// it. Masking on the read instead would be a difference from the original that
    /// nothing could ever see; refusing the value keeps the unreachable state
    /// unreachable, which is the honest half of the pair.
    /// </summary>
    private static int Heading(int value) => value is >= 0 and <= 3
        ? value
        : throw new ArgumentOutOfRangeException(
            nameof(value), value, "A worm's heading is 0 to 3; nothing in the game writes another.");

    /// <summary>
    /// One step of a worm, from <c>0x1194B</c>.
    ///
    /// <code>
    /// 1194b: cmp  WORD PTR ds:0x1612,0x0
    /// 11950: jne  0x11953
    /// 11952: ret
    /// 11953: mov  BYTE PTR ds:0x1698,0x0
    /// 11958: mov  BYTE PTR ds:0x169c,0x0
    /// 1195d: mov  bx,WORD PTR ds:0x1614
    /// 11961: mov  si,WORD PTR ds:0x1610
    /// 11965: mov  WORD PTR ds:0x2c86,si
    /// 11969: shl  si,1
    /// 1196b: mov  di,WORD PTR [si+0x1656]
    /// 1196f: mov  al,BYTE PTR [bx+di]
    /// 11971: cmp  al,0x0
    /// 11973: je   0x119ab                 ; clear ahead - just move
    /// 11975: call 0x11a12                 ; otherwise transform what is there
    /// 11978: call 0x124b1                 ; and pick which way to try turning
    /// </code>
    ///
    /// Note the order of those last two. The transform runs BEFORE the turn draw,
    /// and on a boulder it draws as well, so meeting a boulder takes two numbers
    /// out of the shared stream and meeting anything else takes one. Getting that
    /// count wrong moves every later random event in the cave.
    ///
    /// Note also that the transform does not open the way: the cell in front is
    /// tested once, at <c>0x11971</c>, and a treasure turned into a boulder chain
    /// is still not something the worm can walk into this step.
    /// </summary>
    private void MoveWorm(WormState w)
    {
        if (w.Length == 0) return;                          // 0x11950

        w.Reversed = false;                                 // 0x11953
        w.Blocked = false;                                  // 0x11958

        var bx = w.Head;                                    // 0x1195D

        // ds:0x2C86, the direction being tried. It is a global in the original and
        // a local here: both worms write it and neither reads it after the routine
        // it was set in, so it carries nothing between them.
        var tried = w.Direction;                            // 0x11965
        var di = Delta[w.Direction];                        // 0x1196B - no mask

        if (_grid.ValueAt(bx + di) != RuntimeTile.Space)    // 0x11973
        {
            Transform(w, bx + di, _grid.ValueAt(bx + di));  // 0x11975

            // 0x1197B tests bit zero: set tries the anticlockwise turn first,
            // clear tries the clockwise one first.
            var first = (_random.Next() & 1) != 0 ? -1 : +1;

            // Both attempts start from ds:0x1610 again, not from the first turn -
            // 0x1197F and 0x11988 both reload it. So the worm tries one to each
            // side of where it was heading and never two the same way.
            if (!TryTurn(w, bx, first, ref tried, ref di)
                && !TryTurn(w, bx, -first, ref tried, ref di))
            {
                Stuck(w, bx);                               // 0x11991 -> 0x119EC
                return;
            }
        }

        Advance(w, bx, di, tried);                          // 0x119AB
    }

    /// <summary>
    /// One turn attempt, from <c>0x11A44</c> (anticlockwise) and <c>0x11A56</c>
    /// (clockwise). One routine each way, shared by both worms.
    ///
    /// <code>
    /// 11a44: dec  si
    /// 11a45: and  si,0x3
    /// 11a48: mov  WORD PTR ds:0x2c86,si
    /// 11a4c: shl  si,1
    /// 11a4e: mov  di,WORD PTR [si+0x1656]
    /// 11a52: cmp  BYTE PTR [bx+di],0x0
    /// 11a55: ret
    /// </code>
    ///
    /// It leaves the direction and the delta set whether or not the cell was clear,
    /// which is what lets the caller fall straight into the move with the second
    /// attempt's delta still in <c>di</c>.
    /// </summary>
    private bool TryTurn(WormState w, int bx, int step, ref int tried, ref int di)
    {
        tried = (w.Direction + step) & 3;                   // 0x11A44, 0x11A45
        di = Delta[tried];                                  // 0x11A4E
        return _grid.ValueAt(bx + di) == RuntimeTile.Space; // 0x11A52
    }

    /// <summary>
    /// The move, from <c>0x119AB</c>.
    ///
    /// <code>
    /// 119ab: inc  WORD PTR ds:0x1612
    /// 119af: cmp  WORD PTR ds:0x1612,0x2
    /// 119b4: jne  0x119bf
    /// 119b6: mov  BYTE PTR [bx],0xf3        ; the cell being left becomes the tail
    /// 119b9: mov  WORD PTR ds:0x1616,bx
    /// 119bd: jmp  0x119d4
    /// 119bf: cmp  WORD PTR ds:0x1612,0x8
    /// 119c4: jbe  0x119d4
    /// 119c6: mov  si,WORD PTR ds:0x1616
    /// 119ca: mov  WORD PTR [si],0xff00      ; and past eight, the tail is erased
    /// 119ce: mov  WORD PTR ds:0x1612,0x8
    /// 119d4: mov  ax,ds:0x2c86
    /// 119d7: mov  ds:0x1610,ax
    /// 119da: or   al,0xf8
    /// 119dc: mov  BYTE PTR [bx+0x1],al
    /// 119df: add  bx,di
    /// 119e1: mov  BYTE PTR [bx],0x4f
    /// 119e4: mov  BYTE PTR [bx+0x1],al
    /// 119e7: mov  ds:0x1614,bx
    /// </code>
    ///
    /// Both <c>0xF3</c> and <c>0x4F</c> are written as the value byte alone, and
    /// here that changes nothing - each is followed by its own facing a line later.
    /// It matters one step on, where <c>0x13ED4</c> and <c>0x13ED0</c> resolve those
    /// two seeds and have to leave the facing this routine wrote alone.
    ///
    /// The erase at <c>0x119CA</c> does not advance the tail pointer. It does not
    /// need to: the body handler moves it on every scan, when the segment about to
    /// become the last one passes through <c>A6</c>.
    /// </summary>
    private void Advance(WormState w, int bx, int di, int tried)
    {
        w.Length++;                                         // 0x119AB

        if (w.Length == 2)                                  // 0x119B4
        {
            _grid[bx] = w.Seed;                             // 0x119B6
            w.Tail = bx;                                    // 0x119B9
        }
        else if (w.Length > MaxLength)                      // 0x119C4
        {
            _grid.WriteWord(w.Tail, 0xFF00);                // 0x119CA
            w.Length = MaxLength;                           // 0x119CE
        }

        w.Direction = tried;                                // 0x119D7
        var facing = (byte)(FacingTag | tried);             // 0x119DA

        _grid[bx + 1] = facing;                             // 0x119DC
        bx += di;                                           // 0x119DF
        _grid[bx] = w.Body;                                 // 0x119E1
        _grid[bx + 1] = facing;                             // 0x119E4
        w.Head = bx;                                        // 0x119E7
    }

    /// <summary>
    /// Nowhere to go, from <c>0x119EC</c>.
    ///
    /// <code>
    /// 119ec: mov  si,WORD PTR ds:0x1616
    /// 119f0: or   si,si
    /// 119f2: je   0x11a03
    /// 119f4: mov  WORD PTR ds:0x1614,si     ; head and tail change ends
    /// 119f8: mov  WORD PTR ds:0x1616,bx
    /// 119fc: mov  BYTE PTR ds:0x1698,0xff
    /// 11a01: jmp  0x11a08
    /// 11a03: mov  BYTE PTR ds:0x169c,0xff
    /// 11a08: call 0x124b1
    /// 11a0b: and  ax,0x3
    /// 11a0e: mov  ds:0x1610,ax
    /// </code>
    ///
    /// A worm with a body turns round and crawls back along itself; a worm one cell
    /// long has no other end to become, so it just sits there. Either way it takes
    /// a new direction from the generator, and that is the third of the three draws
    /// this routine can make.
    ///
    /// The test is on the tail POINTER being non-zero rather than on the length,
    /// which is the same thing here only because the loader zeroes it at
    /// <c>0x1087E</c> and the move at <c>0x119B9</c> is the one thing that sets it.
    /// </summary>
    private void Stuck(WormState w, int bx)
    {
        if (w.Tail != 0)                                    // 0x119F2
        {
            w.Head = w.Tail;                                // 0x119F4
            w.Tail = bx;                                    // 0x119F8
            w.Reversed = true;                              // 0x119FC
        }
        else
        {
            w.Blocked = true;                               // 0x11A03
        }

        w.Direction = _random.Next() & 3;                   // 0x11A0B
    }

    /// <summary>
    /// What a worm does to the cell in front of it - <c>0x11A12</c> for the first
    /// and <c>0x11B2F</c> for the second, and they are opposites.
    ///
    /// <code>
    /// 11a12: cmp  al,0x40            ; the first worm
    /// 11a16: cmp  al,0x42
    /// 11a1c: mov  WORD PTR [bx+di],0xff3c    ; treasure -> 3C..3F -> boulder
    /// 11a28: cmp  al,0x44
    /// 11a2c: cmp  al,0x46
    /// 11a32: call 0x124b1
    /// 11a35: test al,0x7
    /// 11a37: jne  0x11a43
    /// 11a39: mov  WORD PTR [bx+di],0xff38    ; boulder -> 38..3B -> enemy
    ///
    /// 11b2f: cmp  al,0x90            ; the second worm
    /// 11b33: mov  WORD PTR [bx+di],0xff34    ; enemy -> 34..37 -> boulder
    /// 11b3f: cmp  al,0x44
    /// 11b43: cmp  al,0x46
    /// 11b49: mov  WORD PTR [bx+di],0xff17    ; boulder -> 17..1A -> treasure
    /// </code>
    ///
    /// Neither writes the thing it is making. Both write the first value of a
    /// counting chain the scan was already walking, so the change takes four more
    /// scans to arrive and is visible while it happens. Every one of those chains
    /// was already ported; this is the only thing that was missing.
    ///
    /// Read them together and they are one pair. The first worm walks treasure back
    /// to boulders and boulders on to <c>0x90</c>, the anticlockwise enemy; the
    /// second walks <c>0x90</c> back to boulders and boulders on to treasure. One
    /// spoils a cave in the direction the other mends it.
    ///
    /// Only the first draws, and only on the boulder branch - one time in eight.
    /// The second converts every boulder it meets, with no draw at all, so the two
    /// routines take a different number of numbers out of the shared stream for the
    /// same encounter.
    /// </summary>
    private void Transform(WormState w, int cell, byte ahead)
    {
        if (w.IsFirst)
        {
            if (ahead is RuntimeTile.Treasure or RuntimeTile.TreasureFalling)
            {
                Convert(cell, 0xFF3C);                      // 0x11A1C
            }
            else if (ahead is RuntimeTile.Boulder or RuntimeTile.BoulderFalling)
            {
                if ((_random.Next() & 7) == 0)              // 0x11A35
                    Convert(cell, 0xFF38);                  // 0x11A39
            }

            return;
        }

        if (ahead == RuntimeTile.EnemyAnticlockwise)
            Convert(cell, 0xFF34);                          // 0x11B33
        else if (ahead is RuntimeTile.Boulder or RuntimeTile.BoulderFalling)
            Convert(cell, 0xFF17);                          // 0x11B49
    }

    /// <summary>Both transforms write the cell and clear its shadow, at
    /// <c>0x11A20</c>, <c>0x11A3D</c>, <c>0x11B37</c> and <c>0x11B4D</c>.</summary>
    private void Convert(int cell, ushort word)
    {
        _grid.WriteWord(cell, word);
        _grid.WriteWord(cell + CaveGrid.ShadowPlane, 0xFFFF);
    }

    /// <summary>
    /// A body cell, from <c>0x13E78</c> (<c>A0-A7</c>) and <c>0x145B3</c>
    /// (<c>B0-B7</c>).
    ///
    /// <code>
    /// 13e78: cmp  BYTE PTR ds:0x1698,0xff
    /// 13e7d: jne  0x13eac
    /// 13e7f: cmp  al,0xa0
    /// 13e83: mov  ah,0xa7
    /// 13e87: cmp  al,0xa7
    /// 13e8b: mov  ah,0xa0
    /// 13e8f: mov  ah,0xa0
    /// 13e91: add  ah,BYTE PTR ds:0x1612
    /// 13e95: dec  ah
    /// 13e97: sub  ah,al
    /// 13e99: add  ah,0xa0
    /// 13e9c: mov  BYTE PTR [si],ah
    /// 13e9e: add  BYTE PTR [si+0x1],0x2
    /// 13ea2: and  BYTE PTR [si+0x1],0xfb
    /// 13ea6: or   BYTE PTR [si+0x1],0xf8
    /// 13eaa: jmp  0x13ecf
    /// 13eac: cmp  BYTE PTR ds:0x169c,0x0
    /// 13eb1: jne  0x13ecf
    /// 13eb3: cmp  al,0xa6
    /// 13eb7: mov  WORD PTR ds:0x1616,si
    /// 13ebb: inc  al
    /// 13ebd: cmp  al,0xa8
    /// 13ebf: jne  0x13ecd
    /// 13ec1: cmp  si,WORD PTR ds:0x1616
    /// 13ec5: je   0x13ecb
    /// 13ec7: mov  al,0x0
    /// 13ecb: mov  al,0xa7
    /// 13ecd: mov  BYTE PTR [si],al
    /// </code>
    ///
    /// The ordinary case is one line: every segment ages by one. <c>A0</c> is the
    /// newest cell and <c>A7</c> the oldest, the movement routine writes a fresh
    /// <c>A0</c> at the head each step, and a segment that ages past <c>A7</c> is
    /// erased - unless it is the cell the tail pointer names, which is pinned at
    /// <c>A7</c> and stays until the movement routine erases it for being the ninth.
    /// That pinning is what keeps a worm's drawn length equal to its counted one.
    ///
    /// The tail pointer moves here rather than in the movement routine: the cell
    /// passing through <c>A6</c> claims it on its way to <c>A7</c>.
    ///
    /// When the worm turned round, every segment is mirrored instead and the whole
    /// body reverses in one scan. <c>0x13E9E</c> does the facing in three
    /// instructions that come to <c>(dir + 2) &amp; 3</c> with the tag put back.
    ///
    /// When it is stuck with no body to turn into, nothing happens at all - the
    /// body does not age, so a trapped worm freezes rather than eating itself.
    /// </summary>
    private void WormBody(int si, byte value, WormState w, byte first, byte last)
    {
        if (w.Reversed)                                     // 0x13E7D
        {
            // The two ends are named rather than computed. At the full eight the
            // formula below would give the same answer; shorter than that it would
            // not, and these two lines are what shipped.
            byte mirrored;
            if (value == first) mirrored = last;            // 0x13E83
            else if (value == last) mirrored = first;       // 0x13E8B
            else
                // Byte arithmetic throughout, and only the LOW byte of the length
                // word: add ah,BYTE PTR ds:0x1612.
                mirrored = (byte)(first + (byte)w.Length - 1 - value + first);

            _grid[si] = mirrored;                           // 0x13E9C

            var facing = (byte)(_grid[si + 1] + 2);         // 0x13E9E
            _grid[si + 1] = (byte)((facing & 0xFB) | 0xF8); // 0x13EA2, 0x13EA6
            return;
        }

        if (w.Blocked) return;                              // 0x13EB1

        if (value == (byte)(last - 1)) w.Tail = si;         // 0x13EB7

        value++;                                            // 0x13EBB

        if (value == (byte)(last + 1))                      // 0x13EBF
            value = si == w.Tail ? last : RuntimeTile.Space;

        _grid[si] = value;                                  // 0x13ECD
    }
}
