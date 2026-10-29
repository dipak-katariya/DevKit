using DevKit.Web.Components;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The fade for newly loaded data: its class is there on the renders right after a load, gone once the fade has had
/// its time, so a later render or a tab switch never replays it.
/// </summary>
public class ArrivalCueTests
{
    [Fact]
    public void Class_IsEmpty_UntilALoadMarksIt()
    {
        var cue = new ArrivalCue(() => 1_000);

        Assert.Equal("", cue.Class);
    }

    [Fact]
    public void Class_IsHandedOut_OnTheRendersRightAfterALoad()
    {
        long now = 1_000;
        var cue = new ArrivalCue(() => now);

        cue.Mark();
        Assert.Equal(ArrivalCue.ArriveClass, cue.Class);

        now += 399;
        Assert.Equal(ArrivalCue.ArriveClass, cue.Class);
    }

    [Fact]
    public void Class_IsGone_OnceTheEntranceHasHadItsTime()
    {
        long now = 1_000;
        var cue = new ArrivalCue(() => now);
        cue.Mark();

        now += 400;

        Assert.Equal("", cue.Class);
    }

    [Fact]
    public void Mark_StartsTheWindowAgain_ForTheNextLoad()
    {
        long now = 1_000;
        var cue = new ArrivalCue(() => now);
        cue.Mark();
        now += 5_000;

        cue.Mark();

        Assert.Equal(ArrivalCue.ArriveClass, cue.Class);
    }
}
