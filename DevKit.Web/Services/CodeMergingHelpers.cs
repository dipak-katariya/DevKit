using System.Text.Json;
using System.Text.RegularExpressions;
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
/// records the branches created for it, including ones created outside this tool.
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

        // Limit of 3 keeps the whole remainder (separators included) as the ref segment, so a
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
/// The branches the merging sheet uses for a repository that has none saved in Settings. The merging target is the
/// newest release branch by version (release/cp-26.5.10 is newer than release/cp-26.5.9), the QA branch is main, and
/// each falls back to main, then to the repository's own default branch.
/// </summary>
public static partial class MergingBranchDefaults
{
    private const string ReleasePrefix = "release/";
    private const string MainBranch = "main";

    /// <summary>The merging target to start on: the newest release branch, else main, else the repository's default.</summary>
    public static string MergingTarget(IReadOnlyCollection<string> branches, string? repoDefaultBranch) =>
        NewestRelease(branches) ?? QaBranch(branches, repoDefaultBranch);

    /// <summary>The QA branch to start on: main, else the repository's default branch; empty when the list has neither.</summary>
    public static string QaBranch(IReadOnlyCollection<string> branches, string? repoDefaultBranch) =>
        Find(branches, MainBranch) ?? Find(branches, repoDefaultBranch) ?? "";

    /// <summary>
    /// The release branch with the highest version, or null when no release branch ends in one. When two share the
    /// version (release/APP_26.4.1.2 and release/CG/APP_26.4.1.2), the one directly under release/ is taken.
    /// </summary>
    public static string? NewestRelease(IEnumerable<string> branches) => branches
        .Where(b => b.StartsWith(ReleasePrefix, StringComparison.OrdinalIgnoreCase))
        .Select(b => (Branch: b, Version: VersionOf(b)))
        .Where(x => x.Version is not null)
        .OrderByDescending(x => x.Version)
        .ThenBy(x => x.Branch.Count(c => c == '/'))
        .ThenBy(x => x.Branch, StringComparer.OrdinalIgnoreCase)
        .Select(x => x.Branch)
        .FirstOrDefault();

    private static Version? VersionOf(string branch)
    {
        var match = TrailingVersion().Match(branch);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }

    /// <summary>The version a release branch ends with, such as 26.5.2 in release/cp-26.5.2.</summary>
    [GeneratedRegex(@"(\d+(?:\.\d+){1,3})$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex TrailingVersion();

    /// <summary>The branch as the list spells it, so the picker shows it selected.</summary>
    private static string? Find(IEnumerable<string> branches, string? wanted) =>
        string.IsNullOrWhiteSpace(wanted)
            ? null
            : branches.FirstOrDefault(b => string.Equals(b, GitRefName.Strip(wanted), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A repository's branches, paged through the x-ms-continuationtoken response header; each page waits its
/// turn at the shared TFS gate in the transport. One request for every ref timed out on repositories
/// holding tens of thousands of them. The page cap keeps a pathological repository from hanging the page;
/// hitting it is reported.
/// </summary>
public static class GitRefListing
{
    private const int PageSize = 1000;
    private const int MaxPages = 20;

    public sealed record Result(List<TfsRef> Refs, bool Truncated);

    /// <summary>Throws on a failed page; the caller decides whether a partial answer is acceptable.</summary>
    public static async Task<Result> ListBranchesAsync(TfsRequestClient req, TfsRepo repo, CancellationToken ct)
    {
        var refs = new List<TfsRef>();
        string? token = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = req.ProjectApi(repo.Project,
                $"git/repositories/{Uri.EscapeDataString(repo.Id)}/refs?filter=heads&$top={PageSize}&api-version={req.Ver}" +
                (token is null ? "" : $"&continuationToken={Uri.EscapeDataString(token)}"));

            var (data, next) = await req.GetJsonPagedAsync(url, ct);
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
/// one separately, so a branch counts as created or deleted only when its own entry says so.
/// </summary>
public static class GitRefUpdate
{
    private const string Succeeded = "succeeded";
    private const string AlreadyGone = "succeededNonExistentRef";
    private const string NotCreated = "Branch not created: ";
    private const string NotConfirmed = "TFS did not confirm the branch.";

    private static readonly Dictionary<string, string> CreateRefusals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["refNameConflict"] = "a branch with that name already exists.",
        ["createBranchPermissionRequired"] = "your token may not create branches in this repository.",
        ["rejectedByPolicy"] = "a branch policy rejected it."
    };

    private static readonly Dictionary<string, string> Explanations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["staleOldObjectId"] = "Not deleted: someone pushed to it since it was loaded. Reload and review it again.",
        ["locked"] = "Not deleted: the branch is locked.",
        ["forcePushRequired"] = "Not deleted: deleting branches in this repository needs the Force push permission.",
        ["writePermissionRequired"] = "Not deleted: you do not have permission to change this repository.",
        ["rejectedByPolicy"] = "Not deleted: a branch policy protects it.",
        ["rejectedByPlugin"] = "Not deleted: a server plug-in rejected the change.",
        [AlreadyGone] = "Already gone: the branch no longer existed."
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
                : new BranchDeleteOutcome(repo.Id, repo.Name, r.Name, false, "Not deleted: TFS did not report on this branch."))
            .ToList();
    }

    /// <summary>
    /// Throws <see cref="TfsResponseException"/>, worded for the page, unless the reply's own entry for
    /// <paramref name="branch"/> says it was created. A missing entry or an unreadable reply is no
    /// confirmation either.
    /// </summary>
    public static void ConfirmCreated(string branch, JsonElement response)
    {
        if (EntryFor(branch, response) is not { } entry)
        {
            throw new TfsResponseException(NotCreated + NotConfirmed);
        }

        var status = TfsJson.Str(entry, "updateStatus");
        if (Confirms(SaysSuccess(entry), status))
        {
            return;
        }

        var reason = CreateReason(status);
        var custom = TfsJson.Str(entry, "customMessage");
        throw new TfsResponseException(NotCreated + (custom.Length == 0 ? reason : $"{reason} TFS says: {custom}"));
    }

    private static JsonElement? EntryFor(string branch, JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return TfsJson.Values(response)
            .Where(e => e.ValueKind == JsonValueKind.Object && GitRefName.Matches(TfsJson.Str(e, "name"), branch))
            .Select(e => (JsonElement?)e)
            .FirstOrDefault();
    }

    private static string CreateReason(string status)
    {
        if (CreateRefusals.TryGetValue(status, out var known))
        {
            return known;
        }
        return status.Length == 0 ? NotConfirmed : $"TFS did not confirm the branch (it answered \"{status}\").";
    }

    private static bool SaysSuccess(JsonElement entry) =>
        entry.TryGetProperty("success", out var flag) && flag.ValueKind == JsonValueKind.True;

    /// <summary>Success with no status or "succeeded"; any other status leaves the outcome in doubt.</summary>
    private static bool Confirms(bool success, string status) =>
        success && (status.Length == 0 || status.Equals(Succeeded, StringComparison.OrdinalIgnoreCase));

    private static BranchDeleteOutcome Describe(TfsRepo repo, string branch, JsonElement entry)
    {
        var success = SaysSuccess(entry);
        var status = TfsJson.Str(entry, "updateStatus");

        if (Confirms(success, status))
        {
            return new BranchDeleteOutcome(repo.Id, repo.Name, branch, true, "Deleted");
        }

        var message = Explanations.TryGetValue(status, out var known)
            ? known
            : Unexplained(TfsJson.Str(entry, "customMessage"), status);
        return new BranchDeleteOutcome(repo.Id, repo.Name, branch, success, message);
    }

    /// <summary>A refusal DevKit has no wording for: TFS's own message, or else the status TFS answered.</summary>
    private static string Unexplained(string custom, string status)
    {
        if (custom.Length > 0)
        {
            return $"Not deleted: {custom}";
        }

        var answer = status.Length > 0 ? status : "failed";
        return $"Not deleted: TFS answered \"{answer}\".";
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

    /// <summary>
    /// Cancels and disposes a source the caller has already replaced, if there is one, without blocking. Replacing it
    /// first keeps a second call that runs during the await from cancelling the new source or leaving one behind.
    /// </summary>
    public static async Task CancelAndDisposeAsync(CancellationTokenSource? replaced)
    {
        if (replaced is null)
        {
            return;
        }

        await replaced.CancelAsync();
        replaced.Dispose();
    }
}

/// <summary>
/// How long a page or panel is open. The TFS reads it starts take <see cref="Token"/>, so closing it stops them;
/// a write started by a click takes <see cref="CancellationToken.None"/> instead, so leaving cannot cut it in half.
/// The token stays readable after <see cref="Dispose"/>, unlike a disposed source's, because a read already
/// running finishes its awaits after the page has gone.
/// </summary>
public sealed class PageLifetime : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private int _closed;

    public PageLifetime()
    {
        Token = _source.Token;
    }

    /// <summary>Cancelled once the page closes.</summary>
    public CancellationToken Token { get; }

    /// <summary>
    /// A token source for one operation of the page, cancelled when the page closes or when the operation is
    /// superseded. Already cancelled when the page has closed.
    /// </summary>
    public CancellationTokenSource CreateLinked() => CancellationTokenSource.CreateLinkedTokenSource(Token);

    /// <summary>Closes the page: every read holding the token is cancelled. Later calls do nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }
        _source.Cancel();
        _source.Dispose();
    }
}
