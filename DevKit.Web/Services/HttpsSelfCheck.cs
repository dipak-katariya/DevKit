using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace DevKit.Web.Services;

/// <summary>
/// Opens one https connection to shared DevKit itself once it listens. It shows whether Windows can serve the
/// certificate at all: one whose private key the account cannot reach still loads, and then every connection fails,
/// which Kestrel reports only at Debug. It also reads which of this machine's names the certificate covers, so the
/// printed link is one browsers accept. The short name comes first: browsers sign in with Windows automatically only
/// for names without dots.
/// </summary>
internal static class HttpsSelfCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The name of this machine the certificate at <paramref name="endpoint"/> covers, or null when the connection
    /// failed or the certificate covers neither name; that is logged at Error, since browsers will fail the same way.
    /// </summary>
    public static async Task<string?> NameToShareAsync(IPEndPoint endpoint, ILogger logger, CancellationToken cancellationToken)
    {
        string? name = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            using var tcp = new TcpClient(endpoint.AddressFamily);
            await tcp.ConnectAsync(endpoint, timeout.Token);
            // Only the names are checked: nothing is sent over this connection, and the server need not trust its
            // own certificate (people's browsers do, once they have installed it).
            await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, _) =>
            {
                name = certificate is X509Certificate2 served ? NameCovered(served) : null;
                return name is not null;
            });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = Environment.MachineName }, timeout.Token);
            return name;
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or SocketException or OperationCanceledException)
        {
            logger.LogError(ex,
                "DevKit listens on https at {Endpoint}, but a test connection to it failed, so browsers will fail too: Windows cannot use the certificate's private key, or the certificate covers neither {Name} nor {FullName}",
                endpoint, Environment.MachineName, DevKitHostingOptions.FullMachineName());
            return null;
        }
    }

    /// <summary>This machine's short name if the certificate covers it, else its full name if covered, else null.</summary>
    internal static string? NameCovered(X509Certificate2 certificate) =>
        new[] { Environment.MachineName, DevKitHostingOptions.FullMachineName() }
            .FirstOrDefault(name => certificate.MatchesHostname(name));
}
