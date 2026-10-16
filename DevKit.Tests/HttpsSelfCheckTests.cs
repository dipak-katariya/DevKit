using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Shared DevKit's test connection to itself over https: which of this machine's names its certificate covers, and an
/// Error in the log when browsers would not get through.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HttpsSelfCheckTests
{
    private static readonly string ShortName = Environment.MachineName;
    private static readonly string FullName = DevKitHostingOptions.FullMachineName();

    /// <summary>
    /// A server certificate for <paramref name="names"/> whose key Windows can serve: a key made in memory is refused
    /// by Windows' TLS, so it goes through a .pfx, which gives it a temporary key file until the certificate is disposed.
    /// </summary>
    private static X509Certificate2 Certificate(params string[] names)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=DevKit test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var alternativeNames = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            alternativeNames.AddDnsName(name);
        }

        request.CertificateExtensions.Add(alternativeNames.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var inMemory = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(inMemory.Export(X509ContentType.Pfx), null);
    }

    private static async Task<string?> CheckAgainst(X509Certificate2 certificate, ILogger logger)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var serving = ServeOnceAsync(listener, certificate);
            var name = await HttpsSelfCheck.NameToShareAsync((IPEndPoint)listener.LocalEndpoint, logger, CancellationToken.None);
            // The server side fails when the check turns a certificate down; only the check's verdict matters here.
            await Record.ExceptionAsync(() => serving);
            return name;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task ServeOnceAsync(TcpListener listener, X509Certificate2 certificate)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var server = new SslStream(client.GetStream());
        await server.AuthenticateAsServerAsync(certificate);
    }

    [Fact]
    public void The_short_name_is_shared_when_the_certificate_covers_it()
    {
        using var both = Certificate(FullName, ShortName);
        using var fullOnly = Certificate(FullName);
        using var other = Certificate("devkit.example.com");

        Assert.Equal(ShortName, HttpsSelfCheck.NameCovered(both));
        Assert.Equal(FullName, HttpsSelfCheck.NameCovered(fullOnly));
        Assert.Null(HttpsSelfCheck.NameCovered(other));
    }

    [Fact]
    public void A_wildcard_certificate_for_this_machines_domain_shares_the_full_name()
    {
        var dot = FullName.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return; // a machine on no domain has no full name a wildcard could cover
        }

        using var wildcard = Certificate("*" + FullName[dot..]);

        Assert.Equal(FullName, HttpsSelfCheck.NameCovered(wildcard));
    }

    [Fact]
    public async Task A_certificate_Windows_can_serve_for_this_machine_gives_its_short_name()
    {
        using var certificate = Certificate(ShortName, FullName);
        var logger = new RecordingLogger<HttpsSelfCheckTests>();

        Assert.Equal(ShortName, await CheckAgainst(certificate, logger));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_certificate_for_other_names_is_an_error_browsers_will_hit_too()
    {
        using var certificate = Certificate("devkit.example.com");
        var logger = new RecordingLogger<HttpsSelfCheckTests>();

        Assert.Null(await CheckAgainst(certificate, logger));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is System.Security.Authentication.AuthenticationException);
    }

    [Fact]
    public async Task Nothing_answering_on_the_port_is_logged_at_error()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closed = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();
        var logger = new RecordingLogger<HttpsSelfCheckTests>();

        Assert.Null(await HttpsSelfCheck.NameToShareAsync(closed, logger, CancellationToken.None));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is SocketException);
    }
}
