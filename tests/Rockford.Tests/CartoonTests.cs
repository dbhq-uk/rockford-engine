using Rockford.Web;
using Xunit;

namespace Rockford.Tests;

/// <summary>
/// Which of the original's three cartoons plays for what the host just reported.
///
/// From <c>docs/FORMATS.md</c>, "When each cartoon plays": a won cave is WIN, and a
/// death is LOSE while the clock is above zero and TIME once it is not. The original
/// tests the clock with <c>jle</c> at <c>0x1E08F</c>, so exactly zero is TIME.
/// </summary>
public class CartoonTests
{
    [Theory]
    [InlineData("hunter-cartoon.png", false, "hunter-cartoon.png")]
    [InlineData("hunter-cartoon.png", true, "hunter-cartoon-x8.png")]
    [InlineData("body-cartoon.png", true, "body-cartoon-x8.png")]
    public void Each_build_plays_its_own_strip_and_VIP_never_the_originals(string strip, bool vip, string expected)
    {
        Assert.Equal(expected, Cartoons.StripFor(strip, vip));
    }

    [Theory]
    [InlineData("out")]
    [InlineData("world complete")]
    public void A_won_cave_plays_the_win_cartoon_whatever_the_clock_says(string message)
    {
        Assert.Equal(CartoonKind.Win, Cartoons.For(message, timeRemaining: 40));
        Assert.Equal(CartoonKind.Win, Cartoons.For(message, timeRemaining: 0));
    }

    [Theory]
    [InlineData("caught")]
    [InlineData("game over")]
    public void A_death_with_time_left_plays_the_lose_cartoon(string message)
    {
        Assert.Equal(CartoonKind.Lose, Cartoons.For(message, timeRemaining: 1));
        Assert.Equal(CartoonKind.Lose, Cartoons.For(message, timeRemaining: 87));
    }

    [Theory]
    [InlineData("caught")]
    [InlineData("game over")]
    public void A_death_with_the_clock_at_zero_or_below_plays_the_time_cartoon(string message)
    {
        // Zero counts: 0x1E08F is a jle, not a jl, and a hazard that kills Rockford
        // in the last second is still a death with no time left.
        Assert.Equal(CartoonKind.Time, Cartoons.For(message, timeRemaining: 0));
        Assert.Equal(CartoonKind.Time, Cartoons.For(message, timeRemaining: -1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Object reference not set to an instance of an object.")]
    public void Anything_else_plays_nothing(string? message)
    {
        // An unrecognised message is the host's error text, and promoting it to a
        // cartoon because it happened to be set is the mistake the outcome overlay
        // already refuses to make.
        Assert.Equal(CartoonKind.None, Cartoons.For(message, timeRemaining: 40));
    }
}
