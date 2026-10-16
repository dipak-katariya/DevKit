using System.Net;
using System.Text;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The TFS services as the app composes them with <see cref="TfsServiceCollectionExtensions.AddTfsServices"/>:
/// every one resolves, every request passes the transport and its one shared gate, and what reaches TFS
/// through that pipeline is exactly what a bare client sends.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class TfsServiceRegistrationTests : IDisposable
{
    private const string CollectionUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string UserInfo = "devkit-registration-user";
    private const string CollectionUrlWithUser = "https://" + UserInfo + "@tfs.example.test/tfs/DefaultCollection";
    private const string FactoryLogCategory = "System.Net.Http.HttpClient";
    private const string StoredPat = "registration-test-pat-7a2e";
    private const string TriedUrl = "https://other.example.test/tfs/Collection/";
    private const string TriedPat = "tried-registration-pat-4b9d";
    private const string TwoProjects = """{"count":2,"value":[{"id":"a","name":"One"},{"id":"b","name":"Two"}]}""";
    private const string Project = "Contoso";
    private const string RepoId = "6f1c2d3e-4a5b-4c6d-8e7f-90a1b2c3d4e5";
    private const string RepoProjectId = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";
    private const string Branch = "feature/1001-add-login";
    private const string WorkItemId = "1001";
    private const int WorkItemNumber = 1001;
    private const string BranchPage = """{"value":[{"name":"refs/heads/main","objectId":"1111111111111111111111111111111111111111"}],"count":1}""";
    private const string NewTask = """{"id":5001}""";

    private static readonly TfsRepo Repo = new() { Id = RepoId, Name = "Contoso.Web", Project = Project };
    private static readonly string BaseSha = new('b', 40);

    public TfsServiceRegistrationTests()
    {
        DeleteSettingsFile();
    }

    public void Dispose() => DeleteSettingsFile();

    [Fact]
    public void Every_TFS_service_and_the_request_client_resolve()
    {
        using var tfs = new RecordingTfsHandler();
        using var provider = Compose(tfs);

        Assert.NotNull(provider.GetRequiredService<TfsApiService>());
        Assert.NotNull(provider.GetRequiredService<CodeMergingService>());
        Assert.NotNull(provider.GetRequiredService<CodeCompareService>());
        Assert.NotNull(provider.GetRequiredService<MergeVerificationService>());
        Assert.NotSame(provider.GetRequiredService<TfsRequestClient>(), provider.GetRequiredService<TfsRequestClient>());
        using var circuit = provider.CreateScope();
        Assert.IsType<TfsApiService>(circuit.ServiceProvider.GetRequiredService<ITfsMetadataSource>());
        Assert.Same(circuit.ServiceProvider.GetRequiredService<SharedTfsData>(), circuit.ServiceProvider.GetRequiredService<SharedTfsData>());
    }

    [Fact]
    public void Every_transport_handler_waits_on_the_one_shared_gate()
    {
        using var tfs = new RecordingTfsHandler();
        using var provider = Compose(tfs);

        using var first = provider.GetRequiredService<TfsTransportHandler>();
        using var second = provider.GetRequiredService<TfsTransportHandler>();

        Assert.Same(TfsGate.Shared, provider.GetRequiredService<TfsGate>());
        Assert.NotSame(first, second);
        Assert.Same(TfsGate.Shared, first.Gate);
        Assert.Same(TfsGate.Shared, second.Gate);
    }

    [Fact]
    public async Task A_TFS_call_goes_through_the_transport_and_the_client_has_no_timeout_of_its_own()
    {
        using var tfs = new RecordingTfsHandler();
        tfs.Reply(HttpStatusCode.OK, TwoProjects);
        var transportLog = new MessageRecordingLogger<TfsTransportHandler>();
        using var provider = Compose(tfs, services => services.AddSingleton<ILogger<TfsTransportHandler>>(transportLog));

        var projects = await provider.GetRequiredService<TfsApiService>().GetProjectsAsync();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(TfsRequestClient));

        Assert.Equal(2, projects.Count);
        var line = Assert.Single(transportLog.Entries);
        Assert.Equal("/tfs/DefaultCollection/_apis/projects", line.Properties["Path"]);
        Assert.Equal(200, line.Properties["Status"]);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    /// <summary>
    /// The factory's own handlers log every request's full URL, and a scope that names it, so they are taken
    /// off the TFS client: the transport's one line is the only trace a request leaves, and it names no secret.
    /// </summary>
    [Fact]
    public async Task Only_the_transport_logs_a_TFS_request_and_nothing_logged_names_its_query_user_or_token()
    {
        using var tfs = new RecordingTfsHandler();
        tfs.Reply(HttpStatusCode.OK, TwoProjects);
        var everything = new RecordingLoggerProvider();
        using var provider = Compose(tfs, services =>
            services.AddLogging(logging => logging.AddProvider(everything).SetMinimumLevel(LogLevel.Trace)));
        provider.GetRequiredService<SettingsService>().Tfs.Url = CollectionUrlWithUser;

        await provider.GetRequiredService<TfsApiService>().GetProjectsAsync();

        var sent = Assert.Single(tfs.Sent);
        Assert.True(sent.CacheBusted);
        Assert.Equal(BasicAuth(StoredPat), sent.Authorization);
        var transport = Assert.Single(everything.ByCategory[typeof(TfsTransportHandler).FullName!].Entries);
        Assert.Equal("/tfs/DefaultCollection/_apis/projects", transport.Properties["Path"]);
        Assert.DoesNotContain(everything.ByCategory, c =>
            c.Key.StartsWith(FactoryLogCategory, StringComparison.Ordinal) && (c.Value.Entries.Count > 0 || c.Value.Scopes.Count > 0));
        var logged = everything.ByCategory.Values.SelectMany(l => l.Entries.Select(e => e.AllText).Concat(l.Scopes)).ToList();
        foreach (var secret in new[] { "api-version", "__rc", UserInfo, "Basic ", StoredPat, BasicToken(StoredPat) })
        {
            Assert.DoesNotContain(logged, text => text.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task A_connection_test_through_the_app_pipeline_sends_the_token_being_tried_to_the_server_being_tried()
    {
        using var tfs = new RecordingTfsHandler();
        tfs.Reply(HttpStatusCode.OK, TwoProjects);
        using var provider = Compose(tfs);

        var check = await provider.GetRequiredService<TfsApiService>().TestConnectionAsync(TriedUrl, TfsAuthMode.Pat, TriedPat);

        Assert.Equal(2, check.ProjectCount);
        var sent = Assert.Single(tfs.Sent);
        Assert.Equal("other.example.test", sent.Host);
        Assert.Equal("/tfs/Collection/_apis/projects", sent.Path);
        Assert.Equal(BasicAuth(TriedPat), sent.Authorization);
    }

    [Fact]
    public async Task Writes_and_branch_listings_reach_TFS_through_the_app_pipeline_exactly_as_over_a_bare_client()
    {
        using var piped = new RecordingTfsHandler { Respond = Answer };
        using var provider = Compose(piped);
        var settings = provider.GetRequiredService<SettingsService>();
        using var bare = new RecordingTfsHandler { Respond = Answer };
        using var http = new HttpClient(bare, disposeHandler: false);

        await RunWritesAsync(new TfsApiService(http, settings, NullLogger<TfsApiService>.Instance));
        await RunWritesAsync(provider.GetRequiredService<TfsApiService>());

        Assert.Equal(7, piped.Sent.Count);
        Assert.Equal(bare.Sent, piped.Sent);
        Assert.All(piped.Sent, s => Assert.Equal(BasicAuth(StoredPat), s.Authorization));
    }

    [Fact]
    public async Task A_Windows_sign_in_goes_through_the_transport_and_its_gate_with_no_token()
    {
        using var tfs = new RecordingTfsHandler();
        tfs.Reply(HttpStatusCode.OK, TwoProjects);
        tfs.Reply(HttpStatusCode.OK, TwoProjects);
        var transportLog = new MessageRecordingLogger<TfsTransportHandler>();
        using var provider = Compose(tfs, services => services.AddSingleton<ILogger<TfsTransportHandler>>(transportLog));
        provider.GetRequiredService<SettingsService>().Tfs.Auth = TfsAuthMode.Windows;
        var api = provider.GetRequiredService<TfsApiService>();

        await api.GetProjectsAsync();
        var check = await api.TestConnectionAsync(TriedUrl, TfsAuthMode.Windows, "");

        Assert.Equal(2, check.ProjectCount);
        Assert.Equal(new[] { "tfs.example.test", "other.example.test" }, tfs.Sent.Select(s => s.Host));
        Assert.All(tfs.Sent, s => Assert.Null(s.Authorization));
        Assert.All(tfs.Sent, s => Assert.True(s.CacheBusted));
        Assert.Equal(2, transportLog.Entries.Count);
    }

    /// <summary>
    /// Built as the app builds them, with nothing swapped: only the Windows sign-in client presents the Windows
    /// account, so a PAT that TFS refuses fails instead of quietly going through as that account.
    /// </summary>
    [Fact]
    public void Only_the_Windows_sign_in_client_presents_the_Windows_account()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SettingsService>();
        services.AddTfsServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var handlers = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        var windows = Chain(handlers.CreateHandler(TfsRequestClient.WindowsSignInClient));
        var pat = Chain(handlers.CreateHandler(nameof(TfsRequestClient)));

        Assert.Contains(windows, h => h is TfsTransportHandler);
        Assert.Contains(pat, h => h is TfsTransportHandler);
        Assert.Same(CredentialCache.DefaultCredentials, Assert.IsType<SocketsHttpHandler>(windows[^1]).Credentials);
        Assert.Null(Assert.IsType<SocketsHttpHandler>(pat[^1]).Credentials);
    }

    /// <summary>Every handler a request passes, outermost first; the last is the one that reaches the network.</summary>
    private static List<HttpMessageHandler> Chain(HttpMessageHandler outermost)
    {
        var chain = new List<HttpMessageHandler> { outermost };
        while (chain[^1] is DelegatingHandler { InnerHandler: { } inner })
        {
            chain.Add(inner);
        }
        return chain;
    }

    /// <summary>
    /// The app's composition with TFS itself swapped for the given stand-in, and the settings pointed at it.
    /// <paramref name="replace"/> can register stand-ins of its own on top.
    /// </summary>
    private static ServiceProvider Compose(RecordingTfsHandler tfs, Action<IServiceCollection>? replace = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SettingsService>();
        services.AddTfsServices();
        services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(() => tfs));
        replace?.Invoke(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var settings = provider.GetRequiredService<SettingsService>();
        settings.Tfs.Url = CollectionUrl;
        settings.Tfs.Pat = StoredPat;
        return provider;
    }

    private static async Task RunWritesAsync(TfsApiService api)
    {
        await api.CreateBranchAsync(Project, RepoId, Branch, BaseSha);
        await api.DeleteBranchesAsync(Repo, new[] { new TfsRef { Name = Branch, ObjectId = BaseSha } });
        await api.LinkWorkItemAsync(Project, WorkItemId, RepoProjectId, RepoId, Branch);
        await api.UpdateWorkItemFieldAsync(Project, WorkItemId, "Microsoft.VSTS.Scheduling.Size", 8.0);
        await api.CreateChildTaskAsync(new ChildTaskRequest(Project, WorkItemId, "DEV : Login page", "Development", @"Contoso\TEAMX", @"Contoso\Sprint 12"));
        await api.ListBranchRefsAsync(Repo);
    }

    /// <summary>The same answer for the same request, so both runs see identical replies.</summary>
    private static (HttpStatusCode Status, string Json) Answer(RecordingTfsHandler.Request sent)
    {
        var refs = sent.Path.EndsWith("/refs", StringComparison.Ordinal);
        return sent.Method switch
        {
            "POST" when refs => RecordingTfsHandler.AcceptEveryRef(sent),
            "GET" when refs => (HttpStatusCode.OK, BranchPage),
            "GET" => (HttpStatusCode.OK, RecordingTfsHandler.WorkItemJson(WorkItemNumber, 3)),
            "PATCH" => (HttpStatusCode.OK, RecordingTfsHandler.WorkItemJson(WorkItemNumber, 4)),
            _ => (HttpStatusCode.OK, NewTask)
        };
    }

    private static string BasicAuth(string pat) => "Basic " + BasicToken(pat);

    private static string BasicToken(string pat) => Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat));

    private static void DeleteSettingsFile() => File.Delete(TestSettingsLocation.SettingsPath);
}
