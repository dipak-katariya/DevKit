using System.Net;
using System.Text;
using System.Text.Json;
using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Test Connection and Save &amp; Connect check a server URL and sign-in before anything is stored: the check
/// must use the token being tried, never the stored one, or no token at all for Windows sign-in, and must leave
/// the settings untouched.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class TfsConnectionTests : IDisposable
{
    private const string StoredUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string StoredPat = "stored-test-pat-3c1d";
    private const string TriedUrl = "https://other.example.test/tfs/Collection/";
    private const string TriedPat = "tried-test-pat-9e4f";
    private const string TwoProjects = """{"count":2,"value":[{"id":"a","name":"One"},{"id":"b","name":"Two"}]}""";
    private const string UserId = "32c6d772-b2a6-4ff7-a330-e349d013a244";

    private readonly CapturingHandler _tfs = new();
    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly TfsApiService _api;

    public TfsConnectionTests()
    {
        DeleteSettingsFile();
        _http = new HttpClient(_tfs, disposeHandler: false);
        _settings = new SettingsService(NullLogger<SettingsService>.Instance);
        _settings.Tfs.Url = StoredUrl;
        _settings.Tfs.Pat = StoredPat;
        _api = new TfsApiService(_http, _settings, NullLogger<TfsApiService>.Instance);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tfs.Dispose();
        DeleteSettingsFile();
    }

    private static void DeleteSettingsFile() => File.Delete(TestSettingsLocation.SettingsPath);

    [Fact]
    public async Task A_connection_test_sends_the_token_being_tried_to_the_server_being_tried()
    {
        _tfs.Reply(HttpStatusCode.OK, TwoProjects);

        var check = await _api.TestConnectionAsync(TriedUrl, TfsAuthMode.Pat, TriedPat);

        Assert.Equal(2, check.ProjectCount);
        var sent = Assert.Single(_tfs.Sent);
        Assert.Equal("other.example.test", sent.Uri.Host);
        Assert.Equal("/tfs/Collection/_apis/projects", sent.Uri.AbsolutePath);
        Assert.StartsWith("?api-version=5.0&", sent.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + TriedPat)), sent.Authorization);
    }

    [Fact]
    public async Task A_connection_test_stores_nothing()
    {
        _tfs.Reply(HttpStatusCode.OK, TwoProjects);

        await _api.TestConnectionAsync(TriedUrl, TfsAuthMode.Pat, TriedPat);

        Assert.Equal(StoredUrl, _settings.Tfs.Url);
        Assert.Equal(StoredPat, _settings.Tfs.Pat);
        Assert.False(File.Exists(TestSettingsLocation.SettingsPath));
    }

    [Fact]
    public async Task A_refused_token_throws_with_its_status_and_the_page_names_the_token()
    {
        _tfs.Reply(HttpStatusCode.Unauthorized, "");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _api.TestConnectionAsync(TriedUrl, TfsAuthMode.Pat, TriedPat));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("token", TfsErrors.Describe(ex), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_that_is_not_a_project_list_is_not_taken_as_connected()
    {
        _tfs.Reply(HttpStatusCode.OK, """{"message":"Welcome"}""");

        var ex = await Assert.ThrowsAsync<TfsResponseException>(() => _api.TestConnectionAsync(TriedUrl, TfsAuthMode.Pat, TriedPat));

        Assert.Equal(ex.Message, TfsErrors.Describe(ex));
    }

    [Fact]
    public async Task A_sign_in_page_instead_of_json_is_not_taken_as_connected()
    {
        _tfs.Reply(HttpStatusCode.OK, "<html><body>Sign in</body></html>");

        var ex = await Assert.ThrowsAnyAsync<JsonException>(() => _api.TestConnectionAsync(TriedUrl, TfsAuthMode.Pat, TriedPat));

        Assert.Equal("TFS sent a reply DevKit couldn't read.", TfsErrors.Describe(ex));
    }

    [Theory]
    [InlineData(StoredUrl, true)]
    [InlineData("HTTPS://TFS.EXAMPLE.TEST/tfs/DefaultCollection/", true)]
    [InlineData("  https://tfs.example.test/tfs/DefaultCollection  ", true)]
    [InlineData(TriedUrl, false)]
    [InlineData("https://tfs.example.test/tfs/OtherCollection", false)]
    public void The_stored_pat_belongs_only_to_its_own_server(string url, bool same)
    {
        Assert.Equal(same, _settings.IsStoredServer(url));
    }

    [Fact]
    public void Whether_a_pat_is_stored_is_known_without_reading_it()
    {
        Assert.True(_settings.HasStoredPat);

        _settings.Tfs.Pat = "";

        Assert.False(_settings.HasStoredPat);
    }

    [Fact]
    public async Task A_Windows_connection_test_sends_no_token_and_names_the_account_TFS_ran_it_as()
    {
        _tfs.Reply(HttpStatusCode.OK, TwoProjects, $@"{UserId}:CONTOSO\dev1");

        var check = await _api.TestConnectionAsync(TriedUrl, TfsAuthMode.Windows, TriedPat);

        Assert.Equal(new TfsConnectionCheck(2, @"CONTOSO\dev1"), check);
        var sent = Assert.Single(_tfs.Sent);
        Assert.Equal("other.example.test", sent.Uri.Host);
        Assert.Equal("/tfs/Collection/_apis/projects", sent.Uri.AbsolutePath);
        Assert.Null(sent.Authorization);
        Assert.Equal(StoredUrl, _settings.Tfs.Url);
        Assert.Equal(TfsAuthMode.Pat, _settings.Tfs.Auth);
        Assert.False(File.Exists(TestSettingsLocation.SettingsPath));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("no-separator", "")]
    [InlineData(UserId + ":dev1", "dev1")]
    [InlineData(UserId + @":CONTOSO\dev1 ", @"CONTOSO\dev1")]
    public async Task The_account_shown_is_the_one_TFS_names_in_its_user_data(string? userData, string account)
    {
        _tfs.Reply(HttpStatusCode.OK, TwoProjects, userData);

        var check = await _api.TestConnectionAsync(TriedUrl, TfsAuthMode.Pat, TriedPat);

        Assert.Equal(account, check.Account);
    }

    [Fact]
    public async Task A_refused_Windows_sign_in_is_described_as_the_Windows_account_not_a_token()
    {
        _tfs.Reply(HttpStatusCode.Unauthorized, "");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _api.TestConnectionAsync(TriedUrl, TfsAuthMode.Windows, ""));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("Windows sign-in", TfsErrors.Describe(ex), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_request_signs_in_the_way_the_settings_say()
    {
        _tfs.Reply(HttpStatusCode.OK, TwoProjects);
        _tfs.Reply(HttpStatusCode.OK, TwoProjects);

        _settings.Tfs.Auth = TfsAuthMode.Windows;
        await _api.GetProjectsAsync();
        _settings.Tfs.Auth = TfsAuthMode.Pat;
        await _api.GetProjectsAsync();

        Assert.Equal(
            new[] { null, "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + StoredPat)) },
            _tfs.Sent.Select(s => s.Authorization));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private const string UserDataHeader = "X-VSS-UserData";

        private readonly Queue<(HttpStatusCode Status, string Body, string? UserData)> _replies = new();

        public sealed record Request(Uri Uri, string? Authorization);

        public List<Request> Sent { get; } = new();

        /// <param name="userData">What TFS sends back as the account the request ran as; null sends no such header.</param>
        public void Reply(HttpStatusCode status, string body, string? userData = null) => _replies.Enqueue((status, body, userData));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(new Request(request.RequestUri!, request.Headers.Authorization?.ToString()));
            var (status, body, userData) = _replies.Dequeue();
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (userData is not null)
            {
                response.Headers.TryAddWithoutValidation(UserDataHeader, userData);
            }
            return Task.FromResult(response);
        }
    }
}
