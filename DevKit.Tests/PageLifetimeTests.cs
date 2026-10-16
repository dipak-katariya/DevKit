using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Closing a page cancels the TFS reads it started. Its token must still be safe to read after it closed: a read
/// that was running finishes its awaits on a page that is gone, and must stop rather than throw or start over.
/// </summary>
public sealed class PageLifetimeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Closing_the_page_ends_a_read_waiting_on_its_token()
    {
        var page = new PageLifetime();
        var read = Task.Delay(Timeout.Infinite, page.Token);
        Assert.False(page.Token.IsCancellationRequested);

        page.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TestTimeout));
        Assert.True(page.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task A_closed_page_still_hands_out_its_token_and_anything_started_on_it_stops_at_once()
    {
        var page = new PageLifetime();
        page.Dispose();

        var token = page.Token;
        var called = false;
        using (token.Register(() => called = true))
        {
            Assert.True(called);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(Timeout.Infinite, token).WaitAsync(TestTimeout));
    }

    [Fact]
    public void An_operation_of_the_page_stops_when_superseded_or_when_the_page_closes()
    {
        using var page = new PageLifetime();
        using var superseded = page.CreateLinked();
        using var running = page.CreateLinked();

        superseded.Cancel();
        Assert.True(superseded.IsCancellationRequested);
        Assert.False(page.Token.IsCancellationRequested);
        Assert.False(running.IsCancellationRequested);

        page.Dispose();
        Assert.True(running.IsCancellationRequested);
    }

    [Fact]
    public void An_operation_started_after_the_page_closed_is_cancelled_from_the_start()
    {
        var page = new PageLifetime();
        page.Dispose();

        using var late = page.CreateLinked();

        Assert.True(late.IsCancellationRequested);
    }

    [Fact]
    public void Closing_twice_is_harmless()
    {
        var page = new PageLifetime();

        page.Dispose();
        page.Dispose();

        Assert.True(page.Token.IsCancellationRequested);
    }
}
