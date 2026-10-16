using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Every TfsApiService read and write hands its token down to the request, so a page that is closed can
/// stop it. Each call is started against a TFS that never answers and is then cancelled: it has to end,
/// where a token dropped on the way would leave it waiting for good.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class TfsApiCancellationTests
{
    private const string CollectionUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string Project = "Contoso";
    private const string AreaPath = @"Contoso\TEAMX";
    private const string SprintPath = @"Contoso\Sprint 12";
    private const string WorkItemId = "1001";
    private const string RepoId = "6f1c2d3e-4a5b-4c6d-8e7f-90a1b2c3d4e5";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static readonly Dictionary<string, Func<TfsApiService, CancellationToken, Task>> Calls = new()
    {
        ["projects"] = (api, ct) => api.GetProjectsAsync(ct),
        ["repositories"] = (api, ct) => api.GetRepositoriesAsync(ct),
        ["areas"] = (api, ct) => api.GetAreasAsync(Project, ct),
        ["iterations"] = (api, ct) => api.GetIterationsAsync(Project, ct),
        ["audit"] = (api, ct) => api.LoadAuditDataAsync(Project, AreaPath, SprintPath, ct),
        ["flag date"] = (api, ct) => api.GetPublishedFlagDateAsync(Project, WorkItemId, ct),
        ["burndown"] = (api, ct) => api.LoadBurndownAsync(Project, AreaPath, SprintPath, ct),
        ["completed work"] = (api, ct) => api.GetCompletedWorkDeltasAsync(Project, WorkItemId, ct),
        ["work items"] = (api, ct) => api.LoadWorkItemsAsync(Project, AreaPath, SprintPath, ct),
        ["planning items"] = (api, ct) => api.LoadPlanningItemsAsync(Project, SprintPath, ct),
        ["child tasks"] = (api, ct) => api.GetChildTasksAsync(Project, WorkItemId, ct),
        ["field update"] = (api, ct) => api.UpdateWorkItemFieldAsync(Project, WorkItemId, "System.Title", "Login page", ct),
        ["task creation"] = (api, ct) =>
            api.CreateChildTaskAsync(new ChildTaskRequest(Project, WorkItemId, "DEV : Login page", "Development", AreaPath, SprintPath), ct),
        ["ref"] = (api, ct) => api.GetRefAsync(Project, RepoId, "refs/heads/main", ct)
    };

    private readonly SettingsService _settings = new(NullLogger<SettingsService>.Instance);

    public TfsApiCancellationTests()
    {
        _settings.Tfs.Url = CollectionUrl;
    }

    public static TheoryData<string> CallNames => new(Calls.Keys);

    [Theory]
    [MemberData(nameof(CallNames))]
    public async Task A_call_ends_when_its_token_is_cancelled(string call)
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tfs = new StubTfs(async (_, ct) =>
        {
            sent.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Only cancellation ends this wait.");
        });
        using var http = new HttpClient(tfs, disposeHandler: false);
        var api = new TfsApiService(http, _settings, NullLogger<TfsApiService>.Instance);
        using var cancel = new CancellationTokenSource();

        var running = Calls[call](api, cancel.Token);
        await sent.Task.WaitAsync(TestTimeout);
        await cancel.CancelAsync();
        var outcome = await Record.ExceptionAsync(() => running.WaitAsync(TestTimeout));

        // Reads that report failures as data rather than throwing may end without an exception; ending is what matters.
        Assert.True(outcome is null or OperationCanceledException, $"{call} ended with {outcome}");
        Assert.Equal(1, tfs.Calls);
    }
}
