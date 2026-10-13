namespace Rockford.Formats;

/// <summary>
/// Reader for the EGA graphics files - <c>.FIL</c> (tile and sprite sheets, one
/// per world) and <c>.CAR</c> (the cartoons: a 6 by 4 grid of 48 by 48
/// frames, of which the game plays the last three rows).
///
/// Every one is exactly 32,000 bytes, which is 320x200 at 4 bits per pixel. The
/// layout is four sequential bitplanes of 8,000 bytes, most significant bit
/// leftmost - a straight dump of EGA video memory rather than a packed format.
///
/// The colours are a separate matter: the game reprograms the EGA palette
/// registers, and those values live in ROCKFORD.EXE, which is EXEPACK-compressed.
/// Decoding with the default EGA palette gives correct pixels in wrong colours.
/// See docs/FORMATS.md.
/// </summary>
public sealed class EgaImage
{
    public const int Width = 320;
    public const int Height = 200;
    public const int PlaneSize = Width * Height / 8;
    public const int FileSize = PlaneSize * 4;

    /// <summary>The default 16-colour EGA palette, as RGB triples.</summary>
    public static readonly (byte R, byte G, byte B)[] DefaultPalette =
    [
        (0, 0, 0), (0, 0, 170), (0, 170, 0), (0, 170, 170),
        (170, 0, 0), (170, 0, 170), (170, 85, 0), (170, 170, 170),
        (85, 85, 85), (85, 85, 255), (85, 255, 85), (85, 255, 255),
        (255, 85, 85), (255, 85, 255), (255, 255, 85), (255, 255, 255),
    ];

    private readonly byte[] _pixels;

    private EgaImage(byte[] pixels) => _pixels = pixels;

    public static EgaImage Load(string path) => Parse(File.ReadAllBytes(path));

    public static EgaImage Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length != FileSize)
            throw new InvalidDataException($"An EGA screen is {FileSize} bytes, got {data.Length}.");

        var pixels = new byte[Width * Height];

        for (var plane = 0; plane < 4; plane++)
        {
            var offset = plane * PlaneSize;
            for (var i = 0; i < PlaneSize; i++)
            {
                var b = data[offset + i];
                if (b == 0) continue;

                var p = i * 8;
                for (var bit = 0; bit < 8; bit++)
                    if ((b & (0x80 >> bit)) != 0)
                        pixels[p + bit] |= (byte)(1 << plane);
            }
        }

        return new EgaImage(pixels);
    }

    /// <summary>Palette index, 0-15, at the given pixel.</summary>
    public byte this[int x, int y] => _pixels[y * Width + x];

    public ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>
    /// Cut a 16x16 tile out of the sheet. Sheets are addressed as a 20x12 grid.
    /// </summary>
    public byte[] GetTile(int column, int row, int size = 16)
    {
        var tile = new byte[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            tile[y * size + x] = _pixels[(row * size + y) * Width + column * size + x];
        return tile;
    }
}
