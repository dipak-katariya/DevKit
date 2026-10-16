using DevKit.Web.Components;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Leaving a Sprint Insights tab while it loads stops the TFS read it started: the read is cancelled rather than
/// left running for a page nobody is looking at, and the cancellation is neither shown nor logged as a failure.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class PanelClosingTests
{
    private const string CollectionUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string Sprint = @"\Contoso\Iteration\Sprint 1";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TfsArea Area = new() { Name = "Web", Path = @"\Contoso\Area\Web", Project = "Contoso" };

    [Fact]
    public Task Leaving_size_check_mid_load_cancels_the_sprint_read_without_an_error() =>
        AssertLeavingCancelsTheLoadAsync<SizeCheckPanel>("Load Sprint");

    [Fact]
    public Task Leaving_task_creation_mid_load_cancels_the_sprint_read_without_an_error() =>
        AssertLeavingCancelsTheLoadAsync<TaskCreationPanel>("Load Requirements");

    private static async Task AssertLeavingCancelsTheLoadAsync<TPanel>(string loadButton)
        where TPanel : Microsoft.AspNetCore.Components.IComponent
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tfs = new StubTfs(async (_, ct) =>
        {
            using var signal = ct.Register(() => cancelled.TrySetResult());
            requested.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Only cancellation ends this wait.");
        });
        using var http = new HttpClient(tfs, disposeHandler: false);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Tfs.Url = CollectionUrl;
        var log = new RecordingLogger<TPanel>();
        var api = new TfsApiService(http, settings, NullLogger<TfsApiService>.Instance);
        using var tfsData = new SharedTfsData(api, NullLogger<SharedTfsData>.Instance);
        using var harness = ComponentHarness.Create(services =>
        {
            services.AddSingleton(api);
            services.AddSingleton(new PageStateService(tfsData));
            services.AddSingleton<ILogger<TPanel>>(log);
        });
        var panel = await harness.RenderAsync<TPanel>(new Dictionary<string, object?> { ["Area"] = Area, ["Sprint"] = Sprint });

        var loading = harness.RaiseAsync(
            panel, e => e.Name == "button" && e.Text.Contains(loadButton, StringComparison.Ordinal), "onclick", new MouseEventArgs());
        await requested.Task.WaitAsync(TestTimeout);
        await harness.CloseAsync(panel);

        await cancelled.Task.WaitAsync(TestTimeout);
        Assert.True(await loading.WaitAsync(TestTimeout));
        Assert.Equal(1, tfs.Calls);
        lock (log.Entries)
        {
            Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug);
        }
    }
}
