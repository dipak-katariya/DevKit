using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevKit.Web.Services;

namespace DevKit.Tests;

/// <summary>
/// A TFS stand-in that records every request as it reached the wire and answers from a queue of canned
/// replies, then from <see cref="Respond"/>. It also builds the reply shapes TFS uses for a work item and
/// for a ref update. Nothing leaves the process.
/// </summary>
internal sealed class RecordingTfsHandler : HttpMessageHandler
{
    private const string CacheBusterParameter = "__rc=";
    private const string NoReply = """{"message":"No reply was queued for this request."}""";

    private readonly ConcurrentQueue<Request> _sent = new();
    private readonly ConcurrentQueue<(HttpStatusCode Status, string Json)> _replies = new();

    /// <summary>One request as sent. The per-call cache-buster is cut off the query; <see cref="CacheBusted"/> says it was there.</summary>
    /// <param name="Authorization">The Authorization header as sent, scheme included; null when there was none.</param>
    public sealed record Request(
        string Method, string Path, string Query, bool CacheBusted, string? MediaType, string? CharSet, string? Body,
        string Host, string? Authorization);

    public IReadOnlyList<Request> Sent => _sent.ToArray();

    public Func<Request, (HttpStatusCode Status, string Json)>? Respond { get; set; }

    public void Reply(HttpStatusCode status, string json) => _replies.Enqueue((status, json));

    /// <summary>A work item at the given revision whose relations point at the given URLs, in that order.</summary>
    public static string WorkItemJson(int id, int rev, params string[] relationUrls)
    {
        var relations = new JsonArray();
        foreach (var url in relationUrls)
        {
            var rel = url.StartsWith("vstfs:", StringComparison.Ordinal) ? "ArtifactLink" : "System.LinkTypes.Hierarchy-Reverse";
            relations.Add(new JsonObject { ["rel"] = rel, ["url"] = url });
        }
        return new JsonObject { ["id"] = id, ["rev"] = rev, ["fields"] = new JsonObject(), ["relations"] = relations }.ToJsonString();
    }

    /// <summary>A ref-update reply with one entry per branch; an empty status or message is left out.</summary>
    public static string RefUpdatesJson(params (string Branch, bool Success, string Status, string Message)[] answers)
    {
        var value = new JsonArray();
        foreach (var answer in answers)
        {
            var entry = new JsonObject { ["name"] = "refs/heads/" + answer.Branch, ["success"] = answer.Success };
            if (answer.Status.Length > 0)
            {
                entry["updateStatus"] = answer.Status;
            }
            if (answer.Message.Length > 0)
            {
                entry["customMessage"] = answer.Message;
            }
            value.Add(entry);
        }
        return new JsonObject { ["value"] = value, ["count"] = answers.Length }.ToJsonString();
    }

    /// <summary>How many refs a ref-update request carried.</summary>
    public static int RefCount(Request sent)
    {
        using var body = JsonDocument.Parse(sent.Body ?? "[]");
        return body.RootElement.GetArrayLength();
    }

    /// <summary>Answers a ref update the way TFS does when it accepts every ref it was sent.</summary>
    public static (HttpStatusCode Status, string Json) AcceptEveryRef(Request sent)
    {
        using var body = JsonDocument.Parse(sent.Body ?? "[]");
        var answers = body.RootElement.EnumerateArray()
            .Select(r => (GitRefName.Strip(r.GetProperty("name").GetString() ?? ""), true, "succeeded", ""))
            .ToArray();
        return (HttpStatusCode.OK, RefUpdatesJson(answers));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("A request without a URL.");

        // TfsRequestClient appends the cache-buster last with a per-call timestamp, so only its presence is kept.
        var marker = uri.Query.IndexOf(CacheBusterParameter, StringComparison.Ordinal);
        var query = marker < 0 ? uri.Query : uri.Query[..(marker - 1)];
        var contentType = request.Content?.Headers.ContentType;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var sent = new Request(request.Method.Method, uri.AbsolutePath, query, marker >= 0, contentType?.MediaType, contentType?.CharSet, body,
            uri.Host, request.Headers.Authorization?.ToString());
        _sent.Enqueue(sent);

        var (status, json) = _replies.TryDequeue(out var queued)
            ? queued
            : Respond?.Invoke(sent) ?? (HttpStatusCode.NotImplemented, NoReply);
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
