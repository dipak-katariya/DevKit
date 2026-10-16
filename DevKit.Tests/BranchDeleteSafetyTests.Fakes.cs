using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using DevKit.Web.Models;

namespace DevKit.Tests;

/// <summary>The TFS stand-in Branch Cleanup's safety tests run against, and what it records.</summary>
public sealed partial class BranchDeleteSafetyTests
{
    private sealed record Recorded(string Method, string Path, NameValueCollection Query, string? ContentType, string Body);

    private sealed record RefUpdate(string Name, string OldObjectId, string NewObjectId);

    /// <summary>Keeps every report in order; Progress&lt;T&gt; would post them to the thread pool.</summary>
    private sealed class RecordingProgress : IProgress<BranchCleanupProgress>
    {
        public List<BranchCleanupProgress> Reports { get; } = new();

        public void Report(BranchCleanupProgress value) => Reports.Add(value);
    }

    /// <summary>
    /// Answers the branch listing, the active pull requests and the ref update the way TFS does: the pull
    /// request read honours $top and $skip, and a ref update reports on each branch separately, refusing one
    /// whose old object id no longer matches the branch.
    /// </summary>
    private sealed class FakeTfs : HttpMessageHandler
    {
        private const string Succeeded = "succeeded";
        private const string AlreadyGone = "succeededNonExistentRef";
        private const string Stale = "staleOldObjectId";

        private int _nextCommit;
        private int _refUpdates;

        public ConcurrentQueue<Recorded> Requests { get; } = new();
        public Dictionary<string, Dictionary<string, string>> Heads { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, object[]> ActivePullRequests { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Unreported { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Ref updates, numbered from 1 in the order they arrive, that TFS fails outright.</summary>
        public HashSet<int> FailedRefUpdates { get; } = new();

        public bool FailPullRequests { get; set; }

        public int RefUpdateCount => Volatile.Read(ref _refUpdates);

        /// <summary>Points each branch at a new commit, creating the branch when it is not there yet.</summary>
        public void Seed(TfsRepo repo, params string[] branches)
        {
            if (!Heads.TryGetValue(repo.Id, out var heads))
            {
                heads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Heads[repo.Id] = heads;
            }
            foreach (var branch in branches)
            {
                heads[branch] = (++_nextCommit).ToString("x40", CultureInfo.InvariantCulture);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("A request without a URL.");
            var query = HttpUtility.ParseQueryString(uri.Query);
            query.Remove("__rc");
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new Recorded(request.Method.Method, uri.AbsolutePath, query, request.Content?.Headers.ContentType?.MediaType, body));

            var repoId = RepositoryIdIn(uri.AbsolutePath);
            if (uri.AbsolutePath.EndsWith("/pullrequests", StringComparison.OrdinalIgnoreCase))
            {
                return FailPullRequests
                    ? Reply(HttpStatusCode.ServiceUnavailable, new { message = "busy" })
                    : Reply(HttpStatusCode.OK, PullRequestPage(repoId, query));
            }
            if (uri.AbsolutePath.EndsWith("/refs", StringComparison.OrdinalIgnoreCase))
            {
                return request.Method == HttpMethod.Post ? UpdateRefs(repoId, body) : Reply(HttpStatusCode.OK, Listing(repoId));
            }
            return Reply(HttpStatusCode.NotFound, new { message = "not found" });
        }

        private object PullRequestPage(string repoId, NameValueCollection query)
        {
            var all = ActivePullRequests.TryGetValue(repoId, out var prs) ? prs : Array.Empty<object>();
            var skip = int.TryParse(query["$skip"], CultureInfo.InvariantCulture, out var s) ? s : 0;
            var top = int.TryParse(query["$top"], CultureInfo.InvariantCulture, out var t) ? t : all.Length;
            var page = all.Skip(skip).Take(top).ToArray();
            return new { count = page.Length, value = page };
        }

        private object Listing(string repoId)
        {
            var value = HeadsOf(repoId)
                .Select(h => new { name = HeadsPrefix + h.Key, objectId = h.Value, creator = new { displayName = "Dev One" } })
                .ToArray();
            return new { count = value.Length, value };
        }

        private HttpResponseMessage UpdateRefs(string repoId, string body)
        {
            if (FailedRefUpdates.Contains(Interlocked.Increment(ref _refUpdates)))
            {
                return Reply(HttpStatusCode.InternalServerError, new { message = "TF400000: The ref update failed." });
            }

            var heads = HeadsOf(repoId);
            var value = (JsonSerializer.Deserialize<List<RefUpdate>>(body, Json) ?? new List<RefUpdate>())
                .Where(update => !Unreported.Contains(update.Name[HeadsPrefix.Length..]))
                .Select(update =>
                {
                    var status = StatusOf(heads, update);
                    return new
                    {
                        name = update.Name,
                        oldObjectId = update.OldObjectId,
                        newObjectId = update.NewObjectId,
                        success = status != Stale,
                        updateStatus = status
                    };
                })
                .ToArray();
            return Reply(HttpStatusCode.OK, new { count = value.Length, value });
        }

        private static string StatusOf(IReadOnlyDictionary<string, string> heads, RefUpdate update)
        {
            if (!heads.TryGetValue(update.Name[HeadsPrefix.Length..], out var current))
            {
                return AlreadyGone;
            }
            return current == update.OldObjectId ? Succeeded : Stale;
        }

        private IReadOnlyDictionary<string, string> HeadsOf(string repoId) =>
            Heads.TryGetValue(repoId, out var heads) ? heads : new Dictionary<string, string>();

        private static string RepositoryIdIn(string path)
        {
            var segments = path.Split('/');
            var at = Array.IndexOf(segments, "repositories");
            return at >= 0 && at + 1 < segments.Length ? Uri.UnescapeDataString(segments[at + 1]) : "";
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, object body) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
