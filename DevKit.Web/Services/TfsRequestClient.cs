using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DevKit.Web.Services;

/// <summary>
/// Shared TFS/Azure DevOps HTTP plumbing: Basic auth from the stored PAT, cache-bypass
/// headers, TFS error-message extraction, and the collection/project URL builders.
/// Every TFS-facing service goes through this so auth and freshness rules live in one place.
/// </summary>
public sealed class TfsRequestClient
{
    private const string ContinuationTokenHeader = "x-ms-continuationtoken";
    private const int ReadChunkBytes = 81920;

    /// <summary>Long enough for the largest file a comparison reads over a slow link, short enough not to hang.</summary>
    private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly SettingsService _settings;

    public TfsRequestClient(HttpClient http, SettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    public string Url => _settings.Tfs.Url.TrimEnd('/');
    public string Ver => _settings.Tfs.ApiVersion;

    public string Api(string path) => $"{Url}/_apis/{path}";
    public string ProjectApi(string project, string path) => $"{Url}/{Uri.EscapeDataString(project)}/_apis/{path}";
    public string ProjectTeamApi(string project, string team, string path) =>
        $"{Url}/{Uri.EscapeDataString(project)}/{Uri.EscapeDataString(team)}/_apis/{path}";

    /// <summary>The branch's page in the TFS web UI; empty for an empty branch name.</summary>
    public string BranchWebUrl(string project, string repoName, string branch) =>
        string.IsNullOrEmpty(branch)
            ? ""
            : $"{Url}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repoName)}?version=GB{Uri.EscapeDataString(branch)}";

    // WIQL string literals are single-quoted; an embedded apostrophe must be doubled,
    // both to keep the query valid for legitimate names (e.g. "Q1'26") and to prevent
    // a crafted path from altering the query.
    public static string WiqlEscape(string? value) => (value ?? "").Replace("'", "''");

    /// <summary>
    /// Work items under any of the given iteration paths. Several sprints are OR-ed, so one query can span
    /// a whole release. With no path it throws rather than quietly matching every sprint.
    /// </summary>
    public static string WiqlIterationClause(IEnumerable<string> paths)
    {
        var clauses = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => $"[System.IterationPath] UNDER '{WiqlEscape(p)}'")
            .ToList();

        if (clauses.Count == 0) throw new InvalidOperationException("At least one sprint must be selected.");
        return clauses.Count == 1 ? clauses[0] : "(" + string.Join(" OR ", clauses) + ")";
    }

    private AuthenticationHeaderValue AuthHeader()
    {
        var cred = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_settings.Tfs.Pat}"));
        return new AuthenticationHeaderValue("Basic", cred);
    }

    /// <summary>Sends an authenticated request and throws a detailed exception on failure.</summary>
    /// <param name="completion">
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> returns before the body is buffered, so a
    /// caller can stop reading a body that turns out to be too large.
    /// </param>
    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage req, CancellationToken ct = default,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        req.Headers.Authorization = AuthHeader();

        // Always bypass HTTP/proxy caches so reads reflect TFS immediately. TFS can sit behind
        // a proxy that serves stale GETs, and every WIQL POST hits the same URL (only the body
        // differs) — a naive cache could return a previous query's IDs, making freshly-changed
        // items lag. GET/POST URLs also get a unique cache-buster for proxies that ignore the
        // no-cache request headers; TFS ignores the extra parameter.
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true, MustRevalidate = true };
        req.Headers.Pragma.ParseAdd("no-cache");
        if ((req.Method == HttpMethod.Get || req.Method == HttpMethod.Post) && req.RequestUri is { } uri)
        {
            var sep = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
            req.RequestUri = new Uri($"{uri}{sep}__rc={DateTime.UtcNow.Ticks}");
        }

        var resp = await _http.SendAsync(req, completion, ct);
        var succeeded = false;
        try
        {
            await EnsureSuccess(resp, ct);
            succeeded = true;
            return resp;
        }
        finally
        {
            // A failed response is not handed back, so it is released here; the caller logs the failure.
            if (!succeeded)
            {
                resp.Dispose();
            }
        }
    }

    public async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await SendAsync(req, ct);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>GET that also surfaces the paging continuation token TFS returns as a header.</summary>
    public async Task<(JsonElement Json, string? Continuation)> GetJsonPagedAsync(string url, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await SendAsync(req, ct);
        var json = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(ct));
        var token = resp.Headers.TryGetValues(ContinuationTokenHeader, out var values)
            ? values.FirstOrDefault()
            : null;
        return (json, string.IsNullOrWhiteSpace(token) ? null : token);
    }

    /// <summary>
    /// The raw body, or null when it is larger than <paramref name="maxBytes"/>. Reading stops at the
    /// limit, so an unexpectedly huge file costs no more memory than the limit allows.
    /// </summary>
    public async Task<byte[]?> GetBytesAsync(string url, long maxBytes, CancellationToken ct = default)
    {
        // HttpClient's own timeout ends once the headers arrive when the body is streamed, and a body
        // that stalls would otherwise hold a TFS slot until someone presses Stop.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(BodyReadTimeout);
        var token = deadline.Token;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/octet-stream");
        using var resp = await SendAsync(req, token, HttpCompletionOption.ResponseHeadersRead);
        if (resp.Content.Headers.ContentLength is long declared && declared > maxBytes)
        {
            return null;
        }

        await using var body = await resp.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);
        try
        {
            var read = await body.ReadAsync(chunk.AsMemory(0, ReadChunkBytes), token);
            while (read > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return null;
                }
                buffer.Write(chunk, 0, read);
                read = await body.ReadAsync(chunk.AsMemory(0, ReadChunkBytes), token);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
        return buffer.ToArray();
    }

    public async Task<JsonElement> PostJsonAsync(string url, object body, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var resp = await SendAsync(req, ct);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync(ct));
    }

    // EnsureSuccessStatusCode() throws with only the status code; TFS returns a useful
    // "message" in the body. Surface it so the page-level handlers can show a real error.
    private static async Task EnsureSuccess(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var detail = "";
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    detail = m.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // Body was not JSON — fall back to the status line below.
        }
        // The status rides on the exception so callers can say what to do about it (a 401 is the
        // token, a 5xx is TFS) without parsing the message.
        throw new HttpRequestException(
            $"TFS request failed ({(int)resp.StatusCode} {resp.ReasonPhrase})" +
            (string.IsNullOrEmpty(detail) ? "" : $": {detail}"),
            null, resp.StatusCode);
    }
}
