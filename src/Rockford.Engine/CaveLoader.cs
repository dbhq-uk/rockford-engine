namespace Rockford.Engine;

/// <summary>
/// Turns the 880 authored bytes of a cave record into the runtime grid, ported from
/// <c>0x10878</c>.
///
/// The original is a flat if/else chain, first match wins, walked with <c>lodsb</c>
/// over the record in row-major order. Authored index <c>row * 40 + col</c> becomes
/// runtime address <c>row * 0x100 + col * 2</c>.
///
/// This is where the old engine went wrong at the root. It had authored <c>0x20</c> as
/// the exit and <c>0x74</c> as the player. The loader says the opposite: <c>0x20</c> is
/// the only case that stores a player position, and <c>0x74</c> becomes the closed exit
/// that the quota opens.
/// </summary>
public sealed class CaveLoader
{
    public const int AuthoredWidth = 40;
    public const int AuthoredHeight = 22;
    public const int RecordSize = AuthoredWidth * AuthoredHeight;   // 880

    /// <summary>The grid the cave was loaded into.</summary>
    public required CaveGrid Grid { get; init; }

    /// <summary>Where the player starts, and where a death respawns them.
    /// <c>ds:0x16BE</c> and <c>ds:0x1E04</c>.</summary>
    public required int PlayerCell { get; init; }

    /// <summary>Set by authored <c>0x10</c>. <c>ds:0x166A</c>.</summary>
    public required bool HasAmoeba { get; init; }

    /// <summary>Cell of the single authored <c>0x70</c>, or -1. <c>ds:0x1614</c>.</summary>
    public required int TimedEntityACell { get; init; }

    /// <summary>Cell of the single authored <c>0x84</c>, or -1. <c>ds:0x161C</c>.</summary>
    public required int TimedEntityBCell { get; init; }

    public static CaveLoader Load(ReadOnlySpan<byte> authored)
    {
        if (authored.Length < RecordSize)
            throw new ArgumentException($"A cave record is {RecordSize} bytes.", nameof(authored));

        var grid = new CaveGrid();

        // 0x108B9 fills the whole 22 rows with 0xFFFF before decoding, so the padding
        // columns of each row read as a non-zero value and nothing grows into them.
        for (var y = 0; y < CaveGrid.Height; y++)
        for (var i = 0; i < CaveGrid.RowStride; i += 2)
            grid.WriteWord(y * CaveGrid.RowStride + i, 0xFFFF);

        var player = -1;
        var hasAmoeba = false;
        var entityA = -1;
        var entityB = -1;
        var src = 0;

        for (var row = 0; row < AuthoredHeight; row++)
        {
            var di = row * CaveGrid.RowStride;

            for (var col = 0; col < AuthoredWidth; col++, di += CaveGrid.CellStride)
            {
                var a = authored[src++];

                switch (a)
                {
                    case 0x00: grid.WriteWord(di, 0xFF00); break;   // space
                    case 0x01: grid.WriteWord(di, 0xFF01); break;   // dirt
                    case 0x02: grid.WriteWord(di, 0xFF02); break;   // steel
                    case 0x03: grid.WriteWord(di, 0xFF03); break;   // ordinary wall

                    // The timed converting wall, not the expanding wall. The old engine
                    // had these two swapped.
                    case 0x04: grid.WriteWord(di, 0xFF74); break;
                    case 0x08: grid.WriteWord(di, 0xFF15); break;   // expanding wall
                    case 0x0C: grid.WriteWord(di, 0xFF04); break;   // pass-through wall

                    case 0x10:                                       // amoeba
                        grid.WriteWord(di, 0xFF48);
                        hasAmoeba = true;
                        break;

                    case 0x20:                                       // the player
                        grid.WriteWord(di, 0x1110);
                        grid.WriteWord(di + CaveGrid.ShadowPlane, 0x1111);
                        player = di;
                        break;

                    case 0x28: grid.WriteWord(di, 0xFF44); break;   // boulder

                    // Three rotations of ONE family, written as two byte stores.
                    case 0x2C or 0x2D: PutFacing(grid, di, RuntimeTile.EnemyClockwise, 0xF8); break;
                    case 0x2E: PutFacing(grid, di, RuntimeTile.EnemyClockwise, 0xFA); break;
                    case 0x30: PutFacing(grid, di, RuntimeTile.EnemyClockwise, 0xFB); break;

                    // The genuinely separate family.
                    case 0x34: PutFacing(grid, di, RuntimeTile.EnemyAnticlockwise, 0xF9); break;
                    case 0x35: PutFacing(grid, di, RuntimeTile.EnemyAnticlockwise, 0xFA); break;
                    case 0x36: PutFacing(grid, di, RuntimeTile.EnemyAnticlockwise, 0xFB); break;
                    case 0x37: PutFacing(grid, di, RuntimeTile.EnemyAnticlockwise, 0xF8); break;

                    case 0x38: grid.WriteWord(di, 0xFF0C); break;   // upward producer

                    case 0x70:
                        grid.WriteWord(di, 0xFF78);
                        entityA = di;
                        break;

                    case 0x74: grid.WriteWord(di, 0xFF07); break;   // the CLOSED EXIT
                    case 0x78: grid.WriteWord(di, 0xFF6C); break;   // multi-treasure
                    case 0x7C: grid.WriteWord(di, 0xFF70); break;   // time collectible

                    case 0x84:
                        grid.WriteWord(di, 0xFF7C);
                        entityB = di;
                        break;

                    case 0x88: grid.WriteWord(di, 0xFF40); break;   // treasure
                    case 0xC4: grid.WriteWord(di, 0xFF08); break;   // downward producer

                    // 0x10A5A. Anything unrecognised is dirt, not space. No shipped cave
                    // reaches this, but it is what the original does.
                    default: grid.WriteWord(di, 0xFF01); break;
                }
            }
        }

        return new CaveLoader
        {
            Grid = grid,
            PlayerCell = player,
            HasAmoeba = hasAmoeba,
            TimedEntityACell = entityA,
            TimedEntityBCell = entityB,
        };
    }

    /// <summary>
    /// Two byte stores rather than a word store, as the original does. The facing byte
    /// is the tag <c>0xF8</c> with the direction in its low two bits; there is no
    /// formula linking the authored byte to it, so the table is literal.
    /// </summary>
    private static void PutFacing(CaveGrid grid, int di, byte value, byte facing)
    {
        grid[di] = value;
        grid[di + 1] = facing;
    }
}
