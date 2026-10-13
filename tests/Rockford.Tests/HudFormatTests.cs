using Rockford.Engine;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The two status-row fields the port formats: the score at <c>0x12261</c> and the
/// lives at <c>0x13891</c>.
///
/// The clock and the quota are not here. Their three-character field pads with a
/// space because BIOS teletype output cannot right-align, and the HUD gets the same
/// alignment from <c>tabular-nums</c> - see the note at the foot of
/// <see cref="HudFormat"/>. The one thing worth checking from those routines is that
/// no cave can overflow their single hundreds digit, and that is asserted against
/// the real files in <see cref="GameDataTests"/>.
/// </summary>
public class HudFormatTests
{
    // ---- the score --------------------------------------------------------

    [Theory]
    [InlineData(0, 0, "000000")]
    [InlineData(0, 450, "000450")]
    [InlineData(0, 9999, "009999")]
    [InlineData(1, 0, "010000")]
    [InlineData(12, 3456, "123456")]
    [InlineData(99, 9999, "999999")]
    public void The_score_is_six_digits_and_zero_padded(int carries, int points, string expected)
    {
        // 0x12273 and 0x12297 both start at '0' and count up, with no space anywhere
        // in the field. The other three fields blank a leading zero; this one does
        // not, and a player sees the difference on the first gem of a new game.
        Assert.Equal(expected, HudFormat.Score(carries, points));
    }

    [Fact]
    public void The_score_is_built_from_the_two_words_and_not_from_their_sum()
    {
        // 0x12267 reads ds:0x1E00 for two digits and 0x12293 reads ds:0x225C for
        // four. That is what the split score is FOR - the two words are the two
        // halves of this field.
        Assert.Equal("070001", HudFormat.Score(7, 1));
    }

    [Theory]
    [InlineData(100, "999999")]
    [InlineData(1000, "999999")]
    public void The_score_display_sticks_at_all_nines(int carries, string expected)
    {
        // 0x1226B clamps the carry count to 99 for the display only. It does not
        // write the clamp back, so the real score carries on underneath and the
        // bonus lives keep arriving from the same counter - 0x12244 reads
        // ds:0x1E00 itself.
        Assert.Equal(expected, HudFormat.Score(carries, 9999));
    }

    [Fact]
    public void A_clamped_display_does_not_stop_the_score_or_the_bonus_lives()
    {
        // The pair that matters: the field saturates and the game does not.
        var game = new GameSession();
        var livesBefore = game.Lives;

        // A hundred carries takes it one past the clamp, and the last award fills
        // the low four digits so the field is at all nines rather than at 990000.
        for (var i = 0; i < 100; i++) game.Award(10_000);
        game.Award(9_999);

        Assert.Equal(1_009_999, game.Score);        // the real score is past it
        Assert.Equal("999999", game.ScoreText);     // the field is not

        // And the lives kept coming, every second carry, because 0x12244 reads the
        // counter the clamp never writes back to.
        Assert.Equal(livesBefore + 50, game.Lives);
    }

    [Fact]
    public void Below_the_clamp_the_field_is_the_score()
    {
        // The other half of the pair, so the test above cannot pass by the format
        // simply always returning all nines.
        var game = new GameSession();
        for (var i = 0; i < 98; i++) game.Award(10_000);
        game.Award(7_654);

        Assert.Equal(987_654, game.Score);
        Assert.Equal("987654", game.ScoreText);
    }

    [Fact]
    public void The_session_formats_its_own_score()
    {
        var game = new GameSession();
        game.Award(450);

        Assert.Equal("000450", game.ScoreText);
    }

    // ---- lives ------------------------------------------------------------

    [Fact]
    public void The_lives_shown_are_the_spares_and_one_fewer_than_the_lives()
    {
        // 0x13895 is a dec before the digits. So a fresh game, which 0xED23 starts
        // with five, shows four - and the last life shows none.
        var game = new GameSession();

        Assert.Equal(GameSession.StartingLives, game.Lives);
        Assert.Equal(GameSession.StartingLives - 1, game.SpareLives);
        Assert.Equal(4, HudFormat.SpareLives(5));
        Assert.Equal(0, HudFormat.SpareLives(1));
    }
}
