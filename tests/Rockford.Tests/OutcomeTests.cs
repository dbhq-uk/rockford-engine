using Rockford.Web;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// The words the outcome overlay shows (#144). "out" is the host's message for a
/// cave that was COMPLETED, and the overlay used to say "out of time" for it - to
/// the player who had just won. A clock-out arrives as "caught", like every death,
/// and is told apart by the clock, as <see cref="Cartoons.For"/> does.
/// </summary>
public class OutcomeTests
{
    [Fact]
    public void A_completed_cave_says_so_and_is_not_out_of_time()
    {
        var o = Outcomes.For("out", timeRemaining: 40, score: 0);

        Assert.NotNull(o);
        Assert.Equal("clear", o!.Value.Kind);
        Assert.Equal("cave clear", o.Value.Word);
    }

    [Fact]
    public void A_death_with_time_left_is_caught()
    {
        var o = Outcomes.For("caught", timeRemaining: 12, score: 0);

        Assert.Equal("caught", o!.Value.Kind);
        Assert.Equal("caught", o.Value.Word);
        Assert.Equal("a life gone", o.Value.Note);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_death_with_the_clock_at_zero_or_below_is_out_of_time(int clock)
    {
        var o = Outcomes.For("caught", clock, score: 0);

        Assert.Equal("time", o!.Value.Kind);
        Assert.Equal("out of time", o.Value.Word);
        Assert.Equal("a life gone", o.Value.Note);
    }

    [Fact]
    public void Game_over_shows_the_score_whatever_the_clock_says()
    {
        var o = Outcomes.For("game over", timeRemaining: 0, score: 1250);

        Assert.Equal("over", o!.Value.Kind);
        Assert.Equal("game over", o.Value.Word);
        Assert.Equal("score 1250", o.Value.Note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("world complete")]
    [InlineData("Object reference not set to an instance of an object.")]
    public void Anything_else_is_not_promoted_to_an_overlay(string? message)
    {
        Assert.Null(Outcomes.For(message, timeRemaining: 40, score: 0));
    }
}
