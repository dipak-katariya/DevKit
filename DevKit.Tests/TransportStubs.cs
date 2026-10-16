using System.Net;

namespace DevKit.Tests;

/// <summary>The far side of the transport: answers each request with the given function and counts them.</summary>
internal sealed class StubTfs : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
    private int _calls;

    public StubTfs(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public int Calls => Volatile.Read(ref _calls);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return _respond(request, cancellationToken);
    }
}

/// <summary>A body written by the given function, which can hold or fail the download; it notes being disposed.</summary>
internal sealed class ScriptedContent : HttpContent
{
    private readonly Func<Stream, CancellationToken, Task> _write;

    public ScriptedContent(Func<Stream, CancellationToken, Task> write)
    {
        _write = write;
    }

    public bool Disposed { get; private set; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        _write(stream, CancellationToken.None);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        _write(stream, cancellationToken);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
