using Rockford.Engine;
using Rockford.Web.Pages;

namespace Rockford.Tests;

/// <summary>
/// The interruption policy's decision, which had none before.
///
/// `game.js` dropped the held keys on blur and that was the whole of it. A
/// hidden tab still gets timers - throttled to whatever the browser allows - so
/// the cave went on advancing in the background and the ordinary result was
/// coming back to a dead player. <see cref="StepPacer"/> stops a catch-up burst
/// AFTER a stall, which is a different problem: it does not stop the steps.
///
/// The decision is separated from the interop precisely so it can be asserted
/// here rather than only observed in a browser.
/// </summary>
public class PausePolicyTests
{
    [Fact]
    public void Play_runs_when_nothing_has_interrupted_it()
    {
        Assert.False(Home.ShouldHold(GameScreen.Playing, menuOpen: false, pageLeft: false));
    }

    [Fact]
    public void Leaving_the_page_holds_the_cave()
    {
        Assert.True(Home.ShouldHold(GameScreen.Playing, menuOpen: false, pageLeft: true));
    }

    [Fact]
    public void Opening_the_game_menu_holds_the_cave()
    {
        // The menu is a decision point - restart, abandon, change the volume -
        // and the cave advancing underneath it is how a player loses a life
        // while reading their options.
        Assert.True(Home.ShouldHold(GameScreen.Playing, menuOpen: true, pageLeft: false));
    }

    [Theory]
    [InlineData(GameScreen.Title)]
    [InlineData(GameScreen.Menu)]
    [InlineData(GameScreen.Interlude)]
    public void Nothing_outside_play_is_ever_held(GameScreen screen)
    {
        // Nothing is at stake on these, and a Resume button over the world
        // chooser is worse than useless: there is no cave to come back to.
        Assert.False(Home.ShouldHold(screen, menuOpen: true, pageLeft: true));
    }

    [Fact]
    public void Either_reason_alone_is_enough()
    {
        // They are independent. Closing the menu must not resume a cave that is
        // also waiting on the player coming back to the page, and vice versa.
        Assert.True(Home.ShouldHold(GameScreen.Playing, menuOpen: true, pageLeft: true));
    }
}
