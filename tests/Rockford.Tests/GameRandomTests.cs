using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The generator is four instructions long, so these tests pin the arithmetic
/// rather than its statistical behaviour. The sequence below was computed from the
/// disassembly at <c>0x124B1</c> independently of the C#.
/// </summary>
public class GameRandomTests
{
    [Fact]
    public void Matches_the_sequence_the_original_produces_from_a_zero_seed()
    {
        var r = new GameRandom(0);
        ushort[] expected = [0x1249, 0x3492, 0x58DB, 0x7D64, 0x81F5, 0xA287, 0xE699, 0x2F1C];

        foreach (var want in expected)
            Assert.Equal(want, r.Next());
    }

    [Fact]
    public void Low_byte_is_what_a_caller_reading_al_sees()
    {
        var full = new GameRandom(0);
        var low = new GameRandom(0);

        for (var i = 0; i < 16; i++)
            Assert.Equal((byte)(full.Next() & 0xFF), low.NextByte());
    }

    [Fact]
    public void Seed_is_the_whole_state_so_a_run_can_be_replayed()
    {
        var r = new GameRandom(0);
        for (var i = 0; i < 50; i++) r.Next();

        var mid = r.Seed;
        var after = new ushort[10];
        for (var i = 0; i < after.Length; i++) after[i] = r.Next();

        var replay = new GameRandom(mid);
        foreach (var want in after)
            Assert.Equal(want, replay.Next());
    }

    [Fact]
    public void Add_then_rotate_right_three_is_what_the_four_instructions_do()
    {
        // add ax,0x9248 ; ror ax,1 ; ror ax,1 ; ror ax,1
        var r = new GameRandom(0x1234);
        var ax = (ushort)(0x1234 + 0x9248);
        var expected = (ushort)((ax >> 3) | (ax << 13));

        Assert.Equal(expected, r.Next());
    }
}
