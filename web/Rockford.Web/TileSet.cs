namespace Rockford.Web;

using Rockford.Engine;

/// <summary>
/// Where each runtime value's sprite sits on the sheet, from the game's own lookup
/// tables rather than from reading the artwork.
///
/// The tables hold EGA planar byte offsets which the startup code rewrites for the
/// current video mode. The extraction does the VGA conversion, so what arrives here
/// is already a pixel position. It has to be a position and not a tile number: the
/// twelve sprite bands sit at y = 4, 24, 28, 48 and then every 16, which is not a
/// grid, and two of them overlap.
/// </summary>
public sealed class TileSet
{
    /// <summary>Nothing to draw.</summary>
    public static readonly int[] Blank = [-1, -1];

    public required int[][] Main { get; init; }
    public required int[][] Player { get; init; }
    public required int[][] EnemyA { get; init; }
    public required int[][] EnemyB { get; init; }
    public required int[][] GroupA { get; init; }
    public required int[][] GroupB { get; init; }
    public required int[] Never { get; init; }

    /// <summary>
    /// The sprite for one cell.
    /// </summary>
    /// <param name="value">The cell's value byte.</param>
    /// <param name="facing">The cell's second byte, which carries a direction.</param>
    /// <param name="frame">The global frame counter.</param>
    /// <param name="playerPose">What the player last did, from <c>ds:0x2256</c>.</param>
    /// <summary>
    /// Where on the sheet the things worth outlining are drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #70. A boulder has to read against the floor it is sitting on, and
    /// neither the redraws nor the original's sixteen colours guarantee it: in
    /// a world where the floor is brown and the rocks are brown, the thing
    /// about to fall on you is a texture.
    /// </para>
    /// <para>
    /// ONLY THE THINGS THAT MOVE OR KILL. Outlining the walls as well would
    /// draw a grid over the whole cave and defeat the purpose - if everything
    /// is outlined then nothing is.
    /// </para>
    /// <para>
    /// Positions rather than values, because the per-frame payload carries
    /// sheet coordinates and nothing else. It is 880 cells ten times a second,
    /// and widening it to say what each one is would cost more than the feature
    /// saves. This is worked out once, here, by the thing that knows.
    /// </para>
    /// </remarks>
    public int[] OutlinedTiles()
    {
        var values = new List<byte> { RuntimeTile.Player, RuntimeTile.PlayerShadow };

        // The four-state families: treasure, boulders, the multi-treasure, the
        // time collectible, and both enemies.
        foreach (var first in new byte[] { 0x40, 0x44, 0x6C, 0x70, 0x8C, 0x90 })
        {
            for (var i = 0; i < 4; i++) values.Add((byte)(first + i));
        }

        // And both worms, every segment and every facing.
        for (var v = 0xA0; v <= 0xB7; v++) values.Add((byte)v);

        var seen = new HashSet<(int, int)>();
        var flat = new List<int>();

        foreach (var value in values)
        {
            // Every frame and facing, because a family's positions are spread
            // across its table and one lookup would name a quarter of them.
            for (var facing = 0; facing < 4; facing++)
            for (var frame = 0; frame < 8; frame++)
            {
                var at = For(value, (byte)facing, frame, frame % 8);
                if (at.Length < 2 || at[0] < 0) continue;
                if (!seen.Add((at[0], at[1]))) continue;
                flat.Add(at[0]);
                flat.Add(at[1]);
            }
        }

        return flat.ToArray();
    }

    /// <summary>
    /// Sheet positions that cast a contact shadow: anything that can fall.
    /// </summary>
    /// <remarks>
    /// #37. A shadow is what gives an object weight and says which cell it is
    /// actually in, and the thing that matters most in this game arrives from
    /// above. The walls do not cast one - they are the room.
    /// </remarks>
    public int[] ShadowedTiles() => PositionsOf(
        [RuntimeTile.Player, RuntimeTile.PlayerShadow,
         0x40, 0x41, 0x42, 0x43,
         0x44, 0x45, 0x46, 0x47,
         0x6C, 0x6D, 0x6E, 0x6F,
         0x70, 0x71, 0x72, 0x73,
         0x8C, 0x8D, 0x8E, 0x8F,
         0x90, 0x91, 0x92, 0x93]);

    /// <summary>
    /// Sheet positions that emit light, each with a colour index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three numbers each: column, row, colour. 0 is fire, 1 is gold, 2 is the
    /// cold blue of the acid.
    /// </para>
    /// <para>
    /// DELIBERATELY SHORT. Everything that glows here genuinely emits - the
    /// fire tap and its three animation states, the acid, and the exit ONCE IT
    /// HAS OPENED. That last one is the single most important state change in a
    /// cave and it currently looks like any other tile; the closed exit is not
    /// in this list, which is what makes the open one mean something.
    /// </para>
    /// </remarks>
    public int[] EmissiveTiles()
    {
        var lit = new List<(byte Value, int Colour)>();

        // The fire tap, four states.
        for (var v = RuntimeTile.FireTap; v <= RuntimeTile.FireTapLast; v++) lit.Add(((byte)v, 0));

        // The acid, which is cold rather than hot.
        foreach (var v in new byte[] { 0x23, 0x24, 0x25, 0x2B }) lit.Add((v, 2));

        // The exit, open only - all four of its states. RuntimeTile.ExitClosed
        // is not here and must not be: the glow IS the state change.
        //
        // Written as 0x06 first, which is not the open exit at all. It is
        // 0x30 to 0x33, and RuntimeTile says so - which is why this reads the
        // constant rather than a number.
        for (var v = RuntimeTile.ExitOpen; v <= RuntimeTile.ExitOpenLast; v++) lit.Add(((byte)v, 1));

        var seen = new HashSet<(int, int)>();
        var flat = new List<int>();

        foreach (var (value, colour) in lit)
        for (var facing = 0; facing < 4; facing++)
        for (var frame = 0; frame < 8; frame++)
        {
            var at = For(value, (byte)facing, frame, 0);
            if (at.Length < 2 || at[0] < 0) continue;
            if (!seen.Add((at[0], at[1]))) continue;
            flat.Add(at[0]);
            flat.Add(at[1]);
            flat.Add(colour);
        }

        return flat.ToArray();
    }

    /// <summary>Every distinct sheet position a set of values can draw at.</summary>
    private int[] PositionsOf(byte[] values)
    {
        var seen = new HashSet<(int, int)>();
        var flat = new List<int>();

        foreach (var value in values)
        for (var facing = 0; facing < 4; facing++)
        for (var frame = 0; frame < 8; frame++)
        {
            var at = For(value, (byte)facing, frame, frame % 8);
            if (at.Length < 2 || at[0] < 0) continue;
            if (!seen.Add((at[0], at[1]))) continue;
            flat.Add(at[0]);
            flat.Add(at[1]);
        }

        return flat.ToArray();
    }

    public int[] For(byte value, byte facing, int frame, int playerPose)
    {
        // 0x11 is a real value the renderer simply skips, so the first frame of the
        // player's death blast is invisible.
        foreach (var skip in Never)
            if (value == skip) return Blank;

        // Above 0xF8 is the fill the unused columns were loaded with.
        if (value >= 0xF8) return Blank;

        if (value == RuntimeTile.Player) return PlayerSprite(frame, playerPose);

        if (value == RuntimeTile.EnemyClockwise)
        {
            // (facing & 3) * 2, plus an animation bit that flips on every draw. The
            // original keeps that bit in the cell's second plane; a frame parity is
            // the same alternation without writing to the grid from the renderer.
            var i = (facing & 3) * 2 + (frame & 1);
            return EnemyA[i % EnemyA.Length];
        }

        if (value == RuntimeTile.EnemyAnticlockwise)
            return EnemyB[frame & 3];

        if (value is >= 0xA0 and <= 0xA7) return Grouped(GroupA, value, 0xA0, 0xA7, facing, frame);
        if (value is >= 0xB0 and <= 0xB7) return Grouped(GroupB, value, 0xB0, 0xB7, facing, frame);

        // The just-moved markers should be cleared before anything draws, because
        // the scan promotes them back on the same pass. Drawing the creature anyway
        // costs nothing and means a creature can never blink out of existence if
        // that ever stops being true - which is a far worse failure than a frame of
        // the wrong animation.
        if (value == RuntimeTile.EnemyClockwiseMoved)
            return EnemyA[((facing & 3) * 2 + (frame & 1)) % EnemyA.Length];

        if (value == RuntimeTile.EnemyAnticlockwiseMoved)
            return EnemyB[frame & 3];

        return value < Main.Length ? Main[value] : Blank;
    }

    /// <summary>
    /// The two eight-state families share one rule: the first and last values get
    /// their own group of eight, everything between shares the middle group.
    /// </summary>
    private static int[] Grouped(int[][] table, byte value, byte first, byte last,
                                 byte facing, int frame)
    {
        var group = value == first ? 0 : value == last ? 2 : 1;
        var i = group * 8 + (facing & 3) * 2 + (frame & 1);
        return table[i % table.Length];
    }

    /// <summary>
    /// The player, from <c>0x12335</c>. The pose is whatever the last action set and
    /// it is not reset per frame, so Rockford holds the attitude he finished in. The
    /// two idle poses cycle four frames at half speed; everything else alternates two.
    /// The idle pose is NOT held - it is redrawn from the generator every tick, so a
    /// standing player flickers between two idle animations. That is the original
    /// (0x11DAC has no latch), not a fault.
    /// </summary>
    private int[] PlayerSprite(int frame, int pose)
    {
        var step = pose < 0x18 ? frame & 1 : (frame & 7) >> 1;
        var i = pose / 2 + step;
        return Player[i % Player.Length];
    }
}
