namespace Rockford.Formats;

/// <summary>
/// Reader for <c>CELLMAPS.BIN</c>, the cave file shipped on every Rockford disk.
///
/// The format is as plain as it gets: 40 records laid end to end, each one 40 by 22
/// bytes in row-major order, one byte per cell, no header and no compression.
/// 40 x 880 is 35,200 bytes, which is the exact size of the shipped file.
///
/// This class hands back the authored bytes and nothing else. Turning them into a
/// playable cave is <c>Rockford.Engine.CaveLoader</c>'s job, because the mapping
/// lives in the executable rather than in this file.
/// </summary>
public sealed class CellmapsFile
{
    public const int Width = 40;
    public const int Height = 22;
    public const int RecordSize = Width * Height;
    public const int RecordCount = 40;
    public const int FileSize = RecordCount * RecordSize;

    /// <summary>Caves in a world, from the record index at <c>0xFE9D</c>.</summary>
    public const int CavesPerWorld = 4;

    /// <summary>
    /// The raw world number behind each of the five entries on the select screen,
    /// from the table at <c>ds:0x26F2</c>.
    ///
    /// This is the part that catches people out. Records are world-major on the RAW
    /// world number, and the menu only reaches the even ones, so the twenty playable
    /// records are 0-3, 8-11, 16-19, 24-27 and 32-35. The twenty in between belong
    /// to the five worlds whose graphics this release never shipped.
    /// </summary>
    public static ReadOnlySpan<int> MenuWorlds => [0, 2, 4, 6, 8];

    private readonly byte[] _data;

    private CellmapsFile(byte[] data) => _data = data;

    public static CellmapsFile Load(string path) => Parse(File.ReadAllBytes(path));

    public static CellmapsFile Parse(byte[] data)
    {
        if (data.Length != FileSize)
            throw new InvalidDataException(
                $"CELLMAPS.BIN is {FileSize} bytes ({RecordCount} records x {RecordSize}), "
                + $"got {data.Length}.");

        return new CellmapsFile(data);
    }

    public int Count => RecordCount;

    /// <summary>The authored bytes of one record.</summary>
    /// <param name="record">0-based record number, 0 to 39.</param>
    public ReadOnlySpan<byte> Record(int record)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(record);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(record, RecordCount);

        return _data.AsSpan(record * RecordSize, RecordSize);
    }

    /// <summary>
    /// The record a world and stage select, using the raw world number rather than
    /// the menu position. Use <see cref="MenuWorlds"/> to go from one to the other.
    /// </summary>
    public static int RecordOf(int rawWorld, int stage) => rawWorld * CavesPerWorld + stage;

    /// <summary>The record behind a menu position and stage.</summary>
    public ReadOnlySpan<byte> PlayableRecord(int menuPosition, int stage) =>
        Record(RecordOf(MenuWorlds[menuPosition], stage));

    /// <summary>Counts cells holding an authored byte, within one record.</summary>
    public int CountIn(int record, byte authored)
    {
        var n = 0;
        foreach (var b in Record(record))
            if (b == authored)
                n++;
        return n;
    }
}
