using Rockford.Engine;
using Rockford.Web;

namespace Rockford.Tests;

/// <summary>
/// The rule #32 says must hold: a state-bearing animation advances with the
/// ENGINE and not with the display.
/// </summary>
/// <remarks>
/// <para>
/// Extra decorative frames may run on the presentation clock. Explosion phases,
/// a transformation, an active wall and a hatching egg may not - those pictures
/// are the game telling the player what is about to happen, and a picture that
/// advanced because another display frame occurred would be saying it at the
/// wrong time.
/// </para>
/// <para>
/// <c>GameHost.Frame</c> is incremented once per simulation step and is the only
/// thing that moves an animation on. That holds today because
/// <see cref="TileSet.For"/> is a pure function of what it is given: it has no
/// counter of its own, so a renderer cannot advance an animation by drawing
/// twice, and the only way to break the rule is to pass something other than the
/// host's frame - which is a visible change at the call site rather than a
/// silent one in here.
/// </para>
/// <para>
/// These tests exist to keep that true. Without them, adding a field to
/// <c>TileSet</c> that ticks on each lookup would be an ordinary-looking change
/// with no test against it.
/// </para>
/// </remarks>
public class TileSetTests
{
    /// <summary>A tile set with enough in each table to index.</summary>
    private static TileSet Sample()
    {
        static int[][] Table(int n, int at) =>
            Enumerable.Range(0, n).Select(i => new[] { at + i * 16, at }).ToArray();

        return new TileSet
        {
            Main = Table(256, 0),
            Player = Table(16, 16),
            EnemyA = Table(8, 32),
            EnemyB = Table(4, 48),
            GroupA = Table(24, 64),
            GroupB = Table(24, 80),
            Never = [0x11],
        };
    }

    /// <summary>Values whose picture IS the simulation's state.</summary>
    /// <remarks>
    /// The explosion phases, both transformation chains, the expanding wall and
    /// both eggs. Each of these is a countdown the player can act on.
    /// </remarks>
    public static TheoryData<byte> StateBearing() =>
    [
        (byte)0x11, (byte)0x12, (byte)0x13, (byte)0x14,   // treasure blast
        (byte)0x1B, (byte)0x1C, (byte)0x1D, (byte)0x1E,   // space blast
        (byte)0x15, (byte)0x16,                           // expanding wall
        (byte)0x78, (byte)0x79, (byte)0x7A, (byte)0x7B,   // the first egg
        (byte)0x7C, (byte)0x7D, (byte)0x7E, (byte)0x7F,   // the second
    ];

    [Theory]
    [MemberData(nameof(StateBearing))]
    public void Drawing_the_same_state_twice_gives_the_same_picture(byte value)
    {
        // The renderer paints between simulation steps - a scroll alone is
        // sixty frames a second against the engine's ten - so every one of
        // these values is looked up many times with the same frame. If the
        // answer moved, the picture would flicker through a countdown the
        // engine had not advanced.
        var tiles = Sample();

        var first = tiles.For(value, 0xFF, frame: 7, playerPose: 0);
        for (var i = 0; i < 60; i++)
        {
            Assert.Equal(first, tiles.For(value, 0xFF, frame: 7, playerPose: 0));
        }
    }

    [Fact]
    public void The_lookup_keeps_no_counter_of_its_own()
    {
        // The whole rule rests on this. With a counter inside, a renderer could
        // advance an animation by drawing twice and no call site would show it.
        var tiles = Sample();

        var before = tiles.For(0x11, 0xFF, frame: 3, playerPose: 0);

        // Plenty of other lookups in between, of everything that has an
        // animation at all.
        for (var i = 0; i < 100; i++)
        {
            tiles.For(RuntimeTile.Player, 0xFF, i, i % 8);
            tiles.For(RuntimeTile.EnemyClockwise, (byte)(i & 3), i, 0);
            tiles.For(RuntimeTile.EnemyAnticlockwise, (byte)(i & 3), i, 0);
            tiles.For(0xA3, (byte)(i & 3), i, 0);
            tiles.For(0xB5, (byte)(i & 3), i, 0);
        }

        Assert.Equal(before, tiles.For(0x11, 0xFF, frame: 3, playerPose: 0));
    }

    [Fact]
    public void A_decorative_animation_is_free_to_advance_on_the_frame()
    {
        // The other half of the rule, and it has to be true or the rule would
        // be "nothing animates". The enemies alternate on frame parity, which
        // is presentation, and they are not state-bearing: which way an enemy
        // is facing is in the facing byte, and the wiggle is only a wiggle.
        var tiles = Sample();

        var even = tiles.For(RuntimeTile.EnemyClockwise, 0, frame: 0, playerPose: 0);
        var odd = tiles.For(RuntimeTile.EnemyClockwise, 0, frame: 1, playerPose: 0);

        Assert.NotEqual(even, odd);
    }

    [Fact]
    public void The_hosts_frame_is_the_only_clock_an_animation_has()
    {
        // GameHost.Frame moves once per simulation step. Nothing else in the
        // engine exposes a counter a renderer could animate from, and this is
        // what would fail if one were added and wired up.
        var host = typeof(GameHost);
        var clocks = host.GetProperties()
            .Where(p => p.PropertyType == typeof(int))
            .Select(p => p.Name)
            .Where(n => n is "Frame" or "Tick" or "Ticks" or "DisplayFrame")
            .ToArray();

        Assert.Equal(["Frame"], clocks);
    }
}
