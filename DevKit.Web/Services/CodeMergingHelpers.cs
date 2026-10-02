using System.Net;
using System.Text.Json;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>A pull request identified by a work item's artifact link.</summary>
public readonly record struct PullRequestArtifactRef(int PullRequestId, string? RepositoryId);

/// <summary>
/// Parses the artifact links TFS attaches to a work item for a pull request. Three formats
/// are in the wild, including one where the repository GUID is absent entirely.
/// </summary>
public static class PrArtifactLink
{
    private const string GitMarker = "PullRequestId";
    private const string CodeReviewMarker = "CodeReviewId";

    /// <summary>
    /// Handles:
    ///   vstfs:///Git/PullRequestId/{projectGuid}/{repoGuid}/{prId}
    ///   vstfs:///Git/PullRequestId/{projectGuid}%2F{repoGuid}%2F{prId}   (URL-encoded)
    ///   vstfs:///CodeReview/CodeReviewId/{projectGuid}/{prId}            (no repo guid)
    /// Returns null when the link is not a pull-request link.
    /// </summary>
    public static PullRequestArtifactRef? TryParse(string? artifactUrl)
    {
        if (string.IsNullOrWhiteSpace(artifactUrl)) return null;

        var isCodeReview = artifactUrl.Contains(CodeReviewMarker, StringComparison.OrdinalIgnoreCase);
        if (!isCodeReview && !artifactUrl.Contains(GitMarker, StringComparison.OrdinalIgnoreCase)) return null;

        var parts = Uri.UnescapeDataString(artifactUrl).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[^1], out var prId) || prId <= 0) return null;

        // On a CodeReviewId link the GUID before the id is the project, not the repository,
        // so the caller has to probe each candidate repo for the pull request.
        string? repoId = null;
        if (!isCodeReview && Guid.TryParse(parts[^2], out var repoGuid)) repoId = repoGuid.ToString();

        return new PullRequestArtifactRef(prId, repoId);
    }
}

/// <summary>
/// Parses the branch artifact links TFS attaches to a work item, which is how a work item
/// records the branches created for it — including ones created outside this tool.
/// </summary>
public static class GitRefArtifactLink
{
    private const string Marker = "/Git/Ref/";

    /// <summary>Ref-kind prefix for a branch. Tags are GT and commits GC, and neither is wanted here.</summary>
    private const string BranchPrefix = "GB";

    /// <summary>
    /// Handles <c>vstfs:///Git/Ref/{projectGuid}/{repoGuid}/GB{branch}</c>, with the branch either
    /// percent-encoded (what TFS and this tool write, because a branch name's slashes belong to one
    /// ref segment) or left literal. Returns null for anything that is not a branch link.
    /// </summary>
    public static WorkItemBranchLink? TryParse(string? artifactUrl)
    {
        if (string.IsNullOrWhiteSpace(artifactUrl)) return null;

        var markerIdx = artifactUrl.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx < 0) return null;

        var tail = artifactUrl[(markerIdx + Marker.Length)..];

        // Some servers encode the separators between the two GUIDs as well, leaving no literal
        // slash to split on. Unescaping once then recovers them.
        if (!tail.Contains('/')) tail = Uri.UnescapeDataString(tail);

        // Limit of 3 keeps the whole remainder — separators included — as the ref segment, so a
        // branch name written with literal slashes survives intact.
        var parts = tail.Split('/', 3);
        if (parts.Length < 3) return null;
        if (!Guid.TryParse(parts[0], out var projectId) || !Guid.TryParse(parts[1], out var repoId)) return null;

        var refSegment = parts[2];
        if (!refSegment.StartsWith(BranchPrefix, StringComparison.Ordinal)) return null;

        var branch = GitRefName.Strip(Uri.UnescapeDataString(refSegment[BranchPrefix.Length..])).Trim();
        return string.IsNullOrEmpty(branch)
            ? null
            : new WorkItemBranchLink
            {
                ProjectId = projectId.ToString(),
                RepositoryId = repoId.ToString(),
                BranchName = branch
            };
    }
}

/// <summary>Comparisons between a git ref name and a bare branch name.</summary>
public static class GitRefName
{
    private const string HeadsPrefix = "refs/heads/";

    public static string Strip(string refName) =>
        refName.StartsWith(HeadsPrefix, StringComparison.OrdinalIgnoreCase) ? refName[HeadsPrefix.Length..] : refName;

    /// <summary>True when a ref name denotes the given branch, with or without the refs/heads/ prefix.</summary>
    public static bool Matches(string refName, string branch) =>
        !string.IsNullOrWhiteSpace(branch)
        && string.Equals(Strip(refName), Strip(branch), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A repository's branches, paged through the x-ms-continuationtoken response header under the shared
/// TFS gate. One request for every ref timed out on repositories holding tens of thousands of them.
/// The page cap keeps a pathological repository from hanging the page; hitting it is reported.
/// </summary>
public static class GitRefListing
{
    private const int PageSize = 1000;
    private const int MaxPages = 20;

    public sealed record Result(List<TfsRef> Refs, bool Truncated);

    /// <summary>Throws on a failed page — the caller decides whether a partial answer is acceptable.</summary>
    public static async Task<Result> ListBranchesAsync(TfsRequestClient req, TfsRepo repo, CancellationToken ct)
    {
        var refs = new List<TfsRef>();
        string? token = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = req.ProjectApi(repo.Project,
                $"git/repositories/{Uri.EscapeDataString(repo.Id)}/refs?filter=heads&$top={PageSize}&api-version={req.Ver}" +
                (token is null ? "" : $"&continuationToken={Uri.EscapeDataString(token)}"));

            var (data, next) = await TfsThrottle.RunAsync(() => req.GetJsonPagedAsync(url, ct), ct);
            foreach (var entry in TfsJson.Values(data))
                if (ParseRef(entry) is { } parsed) refs.Add(parsed);

            if (next is null) return new Result(refs, Truncated: false);
            token = next;
        }
        return new Result(refs, Truncated: true);
    }

    /// <summary>One branch from a refs listing; null for an entry missing its name or object id.</summary>
    public static TfsRef? ParseRef(JsonElement entry)
    {
        var name = GitRefName.Strip(TfsJson.Str(entry, "name"));
        var objectId = TfsJson.Str(entry, "objectId");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(objectId)) return null;

        var creator = entry.TryGetProperty("creator", out var person) && person.ValueKind == JsonValueKind.Object
            ? TfsJson.Str(person, "displayName")
            : "";
        return new TfsRef { Name = name, ObjectId = objectId, Creator = creator };
    }
}

/// <summary>
/// Reads TFS's answer to a ref update. TFS replies 200 even when it refuses a ref and reports on each
/// one separately, so a branch counts as deleted only when its own entry says so.
/// </summary>
public static class GitRefUpdate
{
    private const string Succeeded = "succeeded";
    private const string AlreadyGone = "succeededNonExistentRef";

    private static readonly Dictionary<string, string> Explanations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["staleOldObjectId"] = "Not deleted — someone pushed to it since it was loaded. Reload and review it again.",
        ["locked"] = "Not deleted — the branch is locked.",
        ["forcePushRequired"] = "Not deleted — deleting branches in this repository needs the Force push permission.",
        ["writePermissionRequired"] = "Not deleted — you do not have permission to change this repository.",
        ["rejectedByPolicy"] = "Not deleted — a branch policy protects it.",
        ["rejectedByPlugin"] = "Not deleted — a server plug-in rejected the change.",
        [AlreadyGone] = "Already gone — the branch no longer existed."
    };

    /// <summary>One outcome per requested branch, in request order; a branch TFS did not mention is not deleted.</summary>
    public static List<BranchDeleteOutcome> Outcomes(TfsRepo repo, IReadOnlyList<TfsRef> requested, JsonElement response)
    {
        var answers = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in TfsJson.Values(response))
        {
            var name = GitRefName.Strip(TfsJson.Str(entry, "name"));
            if (name.Length > 0) answers[name] = entry;
        }

        return requested
            .Select(r => answers.TryGetValue(r.Name, out var entry)
                ? Describe(repo, r.Name, entry)
                : new BranchDeleteOutcome(repo.Id, repo.Name, r.Name, false, "Not deleted — TFS did not report on this branch."))
            .ToList();
    }

    private static BranchDeleteOutcome Describe(TfsRepo repo, string branch, JsonElement entry)
    {
        var success = entry.TryGetProperty("success", out var flag) && flag.ValueKind == JsonValueKind.True;
        var status = TfsJson.Str(entry, "updateStatus");

        if (success && (status.Length == 0 || status.Equals(Succeeded, StringComparison.OrdinalIgnoreCase)))
            return new BranchDeleteOutcome(repo.Id, repo.Name, branch, true, "Deleted");

        var custom = TfsJson.Str(entry, "customMessage");
        var message = Explanations.TryGetValue(status, out var known) ? known
            : custom.Length > 0 ? $"Not deleted — {custom}"
            : $"Not deleted — TFS answered \"{(status.Length > 0 ? status : "failed")}\".";
        return new BranchDeleteOutcome(repo.Id, repo.Name, branch, success, message);
    }
}

/// <summary>Replacing the token source that guards a page's in-flight load, the same way on every page.</summary>
public static class CancellationSources
{
    /// <summary>Cancels and disposes the source, if there is one, and clears the reference.</summary>
    public static void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* already torn down */ }
        cts.Dispose();
        cts = null;
    }
}

/// <summary>A TFS reply not shaped the way the API documents it, so nothing read from it can be trusted.</summary>
public sealed class TfsResponseException : Exception
{
    public TfsResponseException(string message) : base(message)
    {
    }
}

/// <summary>
/// What a failed TFS call means for the person looking at the page: what to do about it, never a stack
/// trace or a parser's internals. The full exception still goes to the log.
/// </summary>
public static class TfsErrors
{
    private const int FirstServerError = 500;

    public static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
            "TFS refused the request (401) — check the personal access token in Settings.",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
            "TFS says this token may not read it (403) — check the token's permissions.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            "TFS could not find it (404) — it may have been deleted or renamed.",
        HttpRequestException { StatusCode: { } code } when (int)code >= FirstServerError =>
            $"TFS failed on its side ({(int)code}) — try again in a moment.",
        // Any other status carries TFS's own explanation, which is worth showing as it is.
        HttpRequestException { StatusCode: not null } => ex.Message,
        HttpRequestException => "Couldn't reach TFS — check the server address and the network.",
        // Callers only describe cancellations they did not ask for, so any left here are timeouts.
        OperationCanceledException or TimeoutException => "TFS took too long to answer — try again.",
        TfsResponseException => ex.Message,
        JsonException => "TFS sent a reply DevKit couldn't read.",
        _ => "Something unexpected went wrong — the DevKit log has the details."
    };
}

/// <summary>
/// Process-wide cap on concurrent TFS calls — the code merging sheet, and the Insight Hub's
/// parallel team and work-item reads. Static, not per request: TFS throttles aggressively and a
/// large sprint would otherwise open hundreds of parallel calls. A second user loading a page
/// shares the same slots by design.
/// </summary>
public static class TfsThrottle
{
    private const int MaxParallelRequests = 5;

    private static readonly SemaphoreSlim Gate = new(MaxParallelRequests, MaxParallelRequests);

    public static async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            Gate.Release();
        }
    }
}
