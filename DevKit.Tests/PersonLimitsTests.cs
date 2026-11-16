using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// What one person may take of what everyone shares in shared mode: pages (circuits), whose data the server holds, and
/// the TFS slots every request queues for.
/// </summary>
public sealed class PersonLimitsTests
{
    private static readonly TimeSpan NotYet = TimeSpan.FromMilliseconds(200);

    [Fact]
    public void A_person_gets_no_page_past_the_most_and_others_are_not_affected()
    {
        var circuits = new PersonCircuits();
        for (var page = 0; page < PersonCircuits.MaxPerPerson; page++)
        {
            Assert.True(circuits.TryOpen("alice", $"a{page}"));
        }

        Assert.False(circuits.TryOpen("alice", "one-too-many"));
        Assert.Equal(PersonCircuits.MaxPerPerson, circuits.OpenFor("alice"));
        Assert.True(circuits.TryOpen("bob", "b0"));
    }

    [Fact]
    public void A_closed_page_makes_room_and_one_never_counted_changes_nothing()
    {
        var circuits = new PersonCircuits();
        for (var page = 0; page < PersonCircuits.MaxPerPerson; page++)
        {
            circuits.TryOpen("alice", $"a{page}");
        }

        circuits.Close("never-counted");
        circuits.Close("a0");
        circuits.Close("a0");

        Assert.Equal(PersonCircuits.MaxPerPerson - 1, circuits.OpenFor("alice"));
        Assert.True(circuits.TryOpen("alice", "again"));
        Assert.False(circuits.TryOpen("alice", "past-it-again"));
    }

    [Fact]
    public async Task A_person_holds_at_most_their_share_of_the_TFS_slots_and_the_rest_stay_free_for_others()
    {
        using var gate = new TfsGate(4, perPerson: 2);
        using var first = await gate.EnterAsync(CancellationToken.None, "alice");
        using var second = await gate.EnterAsync(CancellationToken.None, "alice");

        var third = gate.EnterAsync(CancellationToken.None, "alice");
        Assert.False(await Finishes(third));

        using var bobs = await gate.EnterAsync(CancellationToken.None, "bob");
        Assert.Equal(1, gate.Available);

        first.Dispose();
        using var thirdSlot = await third;
        Assert.Equal(1, gate.Available);
    }

    [Fact]
    public async Task Requests_of_nobody_in_particular_share_every_slot_as_before()
    {
        using var gate = new TfsGate(3, perPerson: 1);
        var slots = new List<IDisposable>();
        for (var request = 0; request < 3; request++)
        {
            slots.Add(await gate.EnterAsync(CancellationToken.None));
        }

        Assert.Equal(0, gate.Available);
        slots.ForEach(slot => slot.Dispose());
        Assert.Equal(3, gate.Available);
    }

    [Fact]
    public async Task A_wait_cancelled_for_the_shared_slot_gives_the_persons_own_back()
    {
        using var gate = new TfsGate(1, perPerson: 2);
        using var everyone = await gate.EnterAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();

        var waiting = gate.EnterAsync(stop.Token, "alice");
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        everyone.Dispose();
        using var one = await gate.EnterAsync(CancellationToken.None, "alice");
        Assert.Equal(0, gate.Available);
    }

    [Fact]
    public async Task A_slot_disposed_twice_gives_back_once()
    {
        using var gate = new TfsGate(2, perPerson: 1);
        var slot = await gate.EnterAsync(CancellationToken.None, "alice");

        slot.Dispose();
        slot.Dispose();

        Assert.Equal(2, gate.Available);
        using var again = await gate.EnterAsync(CancellationToken.None, "alice");
        Assert.False(await Finishes(gate.EnterAsync(CancellationToken.None, "alice")));
    }

    [Fact]
    public void A_request_says_whose_it_is_and_one_of_nobody_says_nothing()
    {
        using var mine = new HttpRequestMessage(HttpMethod.Get, "http://tfs.example.test/");
        using var nobodys = new HttpRequestMessage(HttpMethod.Get, "http://tfs.example.test/");

        TfsGate.ForPerson(mine, "alice");
        TfsGate.ForPerson(nobodys, null);

        Assert.Equal("alice", TfsGate.PersonOf(mine));
        Assert.Null(TfsGate.PersonOf(nobodys));
    }

    private static async Task<bool> Finishes(Task task) => await Task.WhenAny(task, Task.Delay(NotYet)) == task;
}
