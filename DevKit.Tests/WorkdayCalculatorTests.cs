using DevKit.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// What the Workday Calculator says about entries it cannot work out: a plain line on how to write them, never the
/// text of an exception, and nothing logged as an error for input that is simply wrong.
/// </summary>
public sealed class WorkdayCalculatorTests : IDisposable
{
    private readonly RecordingLogger<WorkdayCalculator> _log = new();
    private readonly ComponentHarness _harness;

    // 10:00 this morning, local time, as the page reads the clock. Held in UTC: GetLocalNow adds the local offset to it.
    private readonly ManualClock _clock = new() { Now = new DateTimeOffset(DateTime.Today.AddHours(10)).ToUniversalTime() };

    public WorkdayCalculatorTests()
    {
        _harness = ComponentHarness.Create(services => services
            .AddSingleton<ILogger<WorkdayCalculator>>(_log)
            .AddSingleton<TimeProvider>(_clock));
    }

    public void Dispose() => _harness.Dispose();

    // Thousands of pasted lines used to overflow the seconds total, and the page then showed the exception's message.
    [Fact]
    public async Task Too_many_lines_say_so_instead_of_overflowing()
    {
        var page = await _harness.RenderAsync<WorkdayCalculator>(new Dictionary<string, object?>());

        await TypeAsync(page, string.Join('\n', Enumerable.Repeat("09:00\n23:59", 20_000)));

        var elements = await _harness.ElementsAsync(page);
        Assert.Contains(elements, e => e.Attribute("role") == "alert" && e.Text.Contains("Too many lines", StringComparison.Ordinal));
        Assert.DoesNotContain(elements, e => e.Text.Contains("Exception", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Lines_that_are_not_times_say_how_to_write_them()
    {
        var page = await _harness.RenderAsync<WorkdayCalculator>(new Dictionary<string, object?>());

        await TypeAsync(page, "half past nine\nlunch");

        var elements = await _harness.ElementsAsync(page);
        Assert.Contains(elements, e => e.Attribute("role") == "alert" && e.Text.Contains("such as 9:30 or 9:30 AM", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Entries, e => e.Level == LogLevel.Error);
    }

    // A bad line used to be skipped, so the lines after it swapped IN and OUT and the hours came out wrong without a word.
    [Fact]
    public async Task One_line_that_is_not_a_time_stops_the_sums_and_names_the_line()
    {
        var page = await _harness.RenderAsync<WorkdayCalculator>(new Dictionary<string, object?>());

        await TypeAsync(page, "09:00\n12:00\n12;30\n17:00");

        var elements = await _harness.ElementsAsync(page);
        Assert.Contains(elements, e => e.Attribute("role") == "alert" && e.Text.Contains("\"12;30\" is not a time", StringComparison.Ordinal));
        Assert.All(elements.Where(e => e.Name == "dd"), e => Assert.Equal("Unknown", e.Text));
    }

    // A time earlier than the line above it used to be read as crossing midnight: 9:30 then 9:00 counted 23h 30m.
    [Fact]
    public async Task A_time_earlier_than_the_line_above_it_is_named_instead_of_counting_a_day()
    {
        var page = await _harness.RenderAsync<WorkdayCalculator>(new Dictionary<string, object?>());

        await TypeAsync(page, "09:30 AM\n09:00 AM");

        var elements = await _harness.ElementsAsync(page);
        Assert.Contains(elements, e => e.Attribute("role") == "alert"
            && e.Text.Contains("\"09:00 AM\" is earlier than \"09:30 AM\"", StringComparison.Ordinal));
        Assert.All(elements.Where(e => e.Name == "dd"), e => Assert.Equal("Unknown", e.Text));
    }

    // At 10:00 a clock-in at 1:00 PM used to be read as yesterday's and added 21 hours.
    [Fact]
    public async Task A_clock_in_still_to_come_is_named_instead_of_counting_from_yesterday()
    {
        var page = await _harness.RenderAsync<WorkdayCalculator>(new Dictionary<string, object?>());

        await TypeAsync(page, "08:00\n09:00\n13:00");

        var elements = await _harness.ElementsAsync(page);
        Assert.Contains(elements, e => e.Attribute("role") == "alert" && e.Text.Contains("\"13:00\" has not come yet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_open_clock_in_counts_up_to_now()
    {
        var page = await _harness.RenderAsync<WorkdayCalculator>(new Dictionary<string, object?>());

        await TypeAsync(page, "08:00\n09:00\n09:30");

        var elements = await _harness.ElementsAsync(page);
        Assert.DoesNotContain(elements, e => e.Attribute("role") == "alert");
        Assert.Contains(elements, e => e.Name == "dd" && e.Text == "01h 30m 00s");
    }

    [Fact]
    public async Task Readable_times_clear_the_message_and_show_the_hours()
    {
        var page = await _harness.RenderAsync<WorkdayCalculator>(new Dictionary<string, object?>());
        await TypeAsync(page, "lunch");

        await TypeAsync(page, "09:00\n12:00");

        var elements = await _harness.ElementsAsync(page);
        Assert.DoesNotContain(elements, e => e.Attribute("role") == "alert");
        Assert.Contains(elements, e => e.Name == "dd" && e.Text == "03h 00m 00s");
    }

    private Task TypeAsync(int page, string entries) =>
        _harness.RaiseAsync(page, e => e.Name == "textarea", "oninput", new ChangeEventArgs { Value = entries });
}
