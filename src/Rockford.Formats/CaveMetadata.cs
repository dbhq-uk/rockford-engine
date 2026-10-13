using System.Buffers.Binary;

namespace Rockford.Formats;

/// <summary>
/// The per-cave numbers the game keeps in its executable rather than in
/// <c>CELLMAPS.BIN</c>, which is pure cell data with no room for them.
///
/// Four 40-entry tables of little-endian words, indexed by the same record number
/// as the cave file seek. The initialisation code at <c>0xFE9D</c> reads two of
/// them directly:
///
/// <code>
/// fe9d: mov si,WORD PTR ds:0x2c92      ; world
/// fea1: shl si,1
/// fea3: shl si,1                       ; world * 4
/// fea5: add si,WORD PTR ds:0x16b8      ; + stage
/// fea9: shl si,1                       ; word index
/// feab: mov ax,WORD PTR [si+0x26a2]    ; time limit
/// feaf: inc ax                         ; stored as limit + 1
/// feb0: mov ds:0x225e,ax
/// feb3: mov ax,WORD PTR [si+0x245e]    ; required treasure
/// feb7: mov ds:0x164a,ax
/// </code>
///
/// Note the record number is <c>4 * world + stage</c>, so the game is ten worlds
/// of four caves.
///
/// <para>The EGA release holds the same five tables, byte for byte, at different
/// offsets - so which release the image came from has to be worked out rather
/// than assumed. See <see cref="Layout"/>.</para>
/// </summary>
public sealed class CaveMetadata
{
    /// <summary>
    /// Where the five tables sit in one release's unpacked executable.
    ///
    /// <para>These offsets used to be VGA constants with no release attached, and
    /// the only guard on them was a length check. <c>unpacked/ega.bin</c> is
    /// longer than <c>unpacked/vga.bin</c>, so it sailed past that check and the
    /// loader read whatever happened to sit at the VGA addresses - which is
    /// zeros. It returned a fully formed object saying every cave needs no
    /// treasure, allows no time and pays nothing, and threw nothing while doing
    /// it. Both executables are tracked in this repository and <c>ROCKFORD_EXE</c>
    /// is documented in the README, so pointing it at the EGA one was the obvious
    /// thing to try and it silently produced a table of zeros.</para>
    /// </summary>
    /// <param name="Name">The release, for error messages.</param>
    public sealed record Layout(
        string Name,
        int RequiredTreasure,
        int ScoreBeforeQuota,
        int ScoreAfterQuota,
        int TimeLimit,
        int AmoebaCap)
    {
        /// <summary>
        /// The instruction that reads the time-limit table: <c>mov ax,[si+imm16]</c>,
        /// encoded <c>8B 84</c> then the offset little-endian.
        ///
        /// <para>This is how a release is identified, rather than by guessing from
        /// the shape of the numbers. The offsets below were read off this exact
        /// instruction in each binary, so an image that does not contain it is an
        /// image these offsets were never derived from.</para>
        /// </summary>
        public byte[] Signature =>
            [0x8B, 0x84, (byte)(TimeLimit & 0xFF), (byte)(TimeLimit >> 8)];
    }

    /// <summary>
    /// The VGA release. Tables read off the initialisation code at <c>0xFE9D</c>,
    /// quoted in full above; the amoeba cap off <c>0xFE5F</c>.
    /// </summary>
    public static readonly Layout Vga = new(
        Name: "VGA",
        RequiredTreasure: 0x245E,
        ScoreBeforeQuota: 0x24AE,
        ScoreAfterQuota: 0x24FE,
        TimeLimit: 0x26A2,
        AmoebaCap: 0x2274);

    /// <summary>
    /// The EGA release. Its initialisation code at <c>0x1E3D1</c> is the
    /// instruction-for-instruction twin of the VGA routine above - same shifts,
    /// same <c>inc ax</c> on the clock - reading different addresses:
    ///
    /// <code>
    /// 1e3d1: mov si,WORD PTR ds:0x46f4     ; world
    /// 1e3d5: shl si,1
    /// 1e3d7: shl si,1                      ; world * 4
    /// 1e3d9: add si,WORD PTR ds:0x35f0     ; + stage
    /// 1e3dd: shl si,1                      ; word index
    /// 1e3df: mov ax,WORD PTR [si+0x4666]   ; time limit
    /// 1e3e3: inc ax                        ; stored as limit + 1
    /// 1e3e4: mov ds:0x4160,ax
    /// 1e3e7: mov ax,WORD PTR [si+0x4486]   ; required treasure
    /// 1e3eb: mov ds:0x3584,ax
    /// </code>
    ///
    /// <para>The five tables are byte-identical to the VGA release's, so the two
    /// builds ship the same forty caves with the same quotas, clocks and scoring.
    /// The offsets are not a fixed distance apart - 8232 for the three scoring
    /// tables, 8132 for the clock, 8252 for the amoeba cap - so this is a
    /// different data-segment layout rather than a rebase, and one adjusted
    /// constant would not have worked.</para>
    /// </summary>
    public static readonly Layout Ega = new(
        Name: "EGA",
        RequiredTreasure: 0x4486,
        ScoreBeforeQuota: 0x44D6,
        ScoreAfterQuota: 0x4526,
        TimeLimit: 0x4666,
        AmoebaCap: 0x42B0);

    /// <summary>Every release this can read, tried in order.</summary>
    public static readonly IReadOnlyList<Layout> Layouts = [Vga, Ega];

    /// <summary>Which release the loaded image came from.</summary>
    public Layout Release { get; private init; } = Vga;

    public const int CaveCount = 40;
    public const int WorldCount = 10;
    public const int CavesPerWorld = 4;

    private readonly ushort[] _required;
    private readonly ushort[] _scoreBefore;
    private readonly ushort[] _scoreAfter;
    private readonly ushort[] _time;
    private readonly ushort[] _amoebaCap;

    private CaveMetadata(ushort[] required, ushort[] before, ushort[] after,
                         ushort[] time, ushort[] amoebaCap)
    {
        _required = required;
        _scoreBefore = before;
        _scoreAfter = after;
        _time = time;
        _amoebaCap = amoebaCap;
    }

    /// <summary>Reads the tables out of an unpacked ROCKFORD.EXE image.</summary>
    public static CaveMetadata Load(string path) => FromImage(File.ReadAllBytes(path));

    /// <summary>Reads the tables out of an unpacked executable already in memory.</summary>
    /// <exception cref="ArgumentException">
    /// The image is not a release this knows the offsets for. It refuses rather
    /// than reading the VGA addresses anyway, which is what it used to do.
    /// </exception>
    public static CaveMetadata FromImage(ReadOnlySpan<byte> image)
    {
        foreach (var layout in Layouts)
        {
            if (!Fits(image, layout) || !Identifies(image, layout)) continue;

            return new CaveMetadata(
                Words(image, layout.RequiredTreasure, CaveCount),
                Words(image, layout.ScoreBeforeQuota, CaveCount),
                Words(image, layout.ScoreAfterQuota, CaveCount),
                Words(image, layout.TimeLimit, CaveCount),
                Words(image, layout.AmoebaCap, WorldCount))
            { Release = layout };
        }

        throw new ArgumentException(
            "This is not a Rockford executable these offsets were read from. It must "
            + "be an UNPACKED ROCKFORD.EXE from the "
            + string.Join(" or ", Layouts.Select(l => l.Name))
            + " release - see tools/unexepack.py. Refusing rather than reading the "
            + "first layout's addresses regardless, which returns a table of zeros "
            + "and no error.", nameof(image));
    }

    /// <summary>Is the image long enough to hold every table of this layout?</summary>
    private static bool Fits(ReadOnlySpan<byte> image, Layout layout) =>
        image.Length >= Math.Max(
            Math.Max(layout.RequiredTreasure, Math.Max(layout.ScoreBeforeQuota,
                Math.Max(layout.ScoreAfterQuota, layout.TimeLimit))) + 2 * CaveCount,
            layout.AmoebaCap + 2 * WorldCount);

    /// <summary>
    /// Does the image contain the instruction these offsets were read off?
    ///
    /// <para>Identification is by code rather than by the shape of the numbers. A
    /// "do the values look plausible" test would pass on any image with the right
    /// bytes by coincidence, and - more to the point - it is the same kind of
    /// inference that let a table of zeros through in the first place.</para>
    /// </summary>
    private static bool Identifies(ReadOnlySpan<byte> image, Layout layout) =>
        image.IndexOf((ReadOnlySpan<byte>)layout.Signature) >= 0;

    private static ushort[] Words(ReadOnlySpan<byte> d, int off, int count)
    {
        var w = new ushort[count];
        for (var i = 0; i < count; i++)
            w[i] = BinaryPrimitives.ReadUInt16LittleEndian(d[(off + 2 * i)..]);
        return w;
    }

    /// <summary>Treasure needed before the exit opens.</summary>
    public int RequiredTreasure(int cave) => _required[Check(cave)];

    /// <summary>Points for one treasure, given how much of the quota is still outstanding.</summary>
    public int TreasureScore(int cave, int remainingQuota) =>
        remainingQuota > 0 ? _scoreBefore[Check(cave)] : _scoreAfter[Check(cave)];

    /// <summary>Seconds on the clock, as the table holds it.</summary>
    public int TimeLimit(int cave) => _time[Check(cave)];

    /// <summary>Amoeba size cap for the world this cave belongs to.</summary>
    public int AmoebaCap(int cave) => _amoebaCap[Check(cave) / CavesPerWorld];

    /// <summary>The world a cave record belongs to, counting from zero.</summary>
    public static int WorldOf(int cave) => Check(cave) / CavesPerWorld;

    private static int Check(int cave) => cave is >= 0 and < CaveCount
        ? cave
        : throw new ArgumentOutOfRangeException(nameof(cave), cave, "Cave must be 0 to 39.");
}
