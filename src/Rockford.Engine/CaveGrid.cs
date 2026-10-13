namespace Rockford.Engine;

/// <summary>
/// The runtime cave, laid out the way the original lays it out so that offsets
/// quoted from the disassembly port across literally.
///
/// A cell is two bytes - a value byte and a facing/sub-state byte. The horizontal
/// stride is 2 and the vertical stride is <c>0x100</c>, so a row holds 128 cells of
/// which only the leftmost 40 are the cave. A parallel plane sits at <c>+0x80</c>
/// within the row; the player writes its second half there.
///
/// Keeping that addressing means a handler written as
/// <c>cmp BYTE PTR [bx+0xfe],0x0</c> becomes <c>this[addr + 0xFE] == 0</c> with no
/// arithmetic to get wrong, and the port can be checked line by line against
/// objdump output.
/// </summary>
public sealed class CaveGrid
{
    public const int Width = 40;
    public const int Height = 22;

    /// <summary>Bytes between one row and the next, as the original uses.</summary>
    public const int RowStride = 0x100;

    /// <summary>Bytes between one cell and the next.</summary>
    public const int CellStride = 2;

    /// <summary>Offset from a cell to its second plane.</summary>
    public const int ShadowPlane = 0x80;

    /// <summary>Two rows of padding, so a handler reaching <c>+0x200</c> from the
    /// last real row stays inside the buffer. The padding is steel, which nothing
    /// moves into and nothing destroys.</summary>
    private const int PaddingRows = 2;

    private readonly byte[] _mem = new byte[(Height + PaddingRows) * RowStride];

    public CaveGrid()
    {
        // Steel everywhere first, so the padding rows and the unused right-hand part
        // of each row behave like the wall the caves are enclosed in.
        for (var i = 0; i < _mem.Length; i += CellStride)
        {
            _mem[i] = RuntimeTile.Steel;
            _mem[i + 1] = 0xFF;
        }
    }

    /// <summary>The address of a cell, in the original's terms.</summary>
    public static int AddressOf(int x, int y) => y * RowStride + x * CellStride;

    public static int ColumnOf(int address) => (address % RowStride) / CellStride;

    public static int RowOf(int address) => address / RowStride;

    /// <summary>True when an address is one of the 40 by 22 cells the cave occupies.</summary>
    public static bool IsInCave(int address)
    {
        var x = ColumnOf(address);
        var y = RowOf(address);
        return x < Width && y < Height && address % CellStride == 0;
    }

    /// <summary>A raw byte at an address. Handlers index with the original's offsets.</summary>
    public byte this[int address]
    {
        get => _mem[address];
        set => _mem[address] = value;
    }

    /// <summary>The value byte of the cell at an address.</summary>
    public byte ValueAt(int address) => _mem[address];

    /// <summary>The facing/sub-state byte of the cell at an address.</summary>
    public byte StateAt(int address) => _mem[address + 1];

    /// <summary>
    /// Writes a whole cell, the way <c>mov WORD PTR [bx],0xff46</c> does: the low
    /// byte is the value and the high byte is the state.
    /// </summary>
    public void WriteWord(int address, ushort word)
    {
        _mem[address] = (byte)(word & 0xFF);
        _mem[address + 1] = (byte)(word >> 8);
    }

    /// <summary>Reads a whole cell as a word, so a handler can compare against
    /// <c>0xFF44</c> rather than testing the two bytes separately. Several do, and
    /// whether they compare the word or only the low byte changes behaviour.</summary>
    public ushort ReadWord(int address) => (ushort)(_mem[address] | (_mem[address + 1] << 8));

    public byte ValueAt(int x, int y) => ValueAt(AddressOf(x, y));

    public byte StateAt(int x, int y) => StateAt(AddressOf(x, y));

    public void WriteWord(int x, int y, ushort word) => WriteWord(AddressOf(x, y), word);

    /// <summary>Every cell address in the cave, in the original's scan order:
    /// top to bottom, left to right.</summary>
    public IEnumerable<int> ScanOrder()
    {
        for (var y = 0; y < Height; y++)
        {
            var row = y * RowStride;
            for (var x = 0; x < Width; x++)
                yield return row + x * CellStride;
        }
    }

    /// <summary>Counts cells whose value byte equals <paramref name="value"/>.</summary>
    public int Count(byte value)
    {
        var n = 0;
        foreach (var a in ScanOrder())
            if (_mem[a] == value)
                n++;
        return n;
    }

    /// <summary>A copy, for tests and for anything that wants to look without racing
    /// the scan.</summary>
    public CaveGrid Clone()
    {
        var c = new CaveGrid();
        Array.Copy(_mem, c._mem, _mem.Length);
        return c;
    }
}
