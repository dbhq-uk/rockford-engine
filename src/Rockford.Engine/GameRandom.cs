namespace Rockford.Engine;

/// <summary>
/// The original game's random number generator, ported instruction for instruction
/// from <c>0x124B1</c> in the unpacked VGA executable:
///
/// <code>
/// 124b1: mov ax,ds:0x2260
/// 124b4: add ax,0x9248
/// 124b7: ror ax,1
/// 124b9: ror ax,1
/// 124bb: ror ax,1
/// 124bd: mov ds:0x2260,ax
/// 124c0: ret
/// </code>
///
/// It is a single 16-bit word: add a constant, rotate right three places, store it
/// back, and hand the whole word to the caller. Callers test either <c>AL</c> or the
/// full <c>AX</c>, and which one they test changes the odds, so both are exposed
/// rather than folded into one "next" method.
/// </summary>
public sealed class GameRandom
{
    private const ushort Addend = 0x9248;

    private ushort _seed;

    public GameRandom(ushort seed = 0) => _seed = seed;

    /// <summary>The seed, for tests that need to pin a sequence down.</summary>
    public ushort Seed
    {
        get => _seed;
        set => _seed = value;
    }

    /// <summary>One draw. Returns the full 16-bit word, as <c>AX</c> does.</summary>
    public ushort Next()
    {
        var ax = (ushort)(_seed + Addend);
        ax = (ushort)((ax >> 3) | (ax << 13));
        _seed = ax;
        return ax;
    }

    /// <summary>One draw, keeping only the low byte, as a caller reading <c>AL</c> does.</summary>
    public byte NextByte() => (byte)(Next() & 0xFF);
}
