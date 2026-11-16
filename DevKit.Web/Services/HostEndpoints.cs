using System.Net;

namespace DevKit.Web.Services;

/// <summary>
/// The addresses DevKit's web server listens on, read from configuration as Kestrel reads them: the endpoints in
/// devkit.json's "Kestrel" section when it has any, otherwise --urls (or ASPNETCORE_URLS), otherwise Kestrel's default.
/// </summary>
internal static class HostEndpoints
{
    private const string EndpointsSection = "Kestrel:Endpoints";
    private const string UrlKey = "Url";
    private const string UrlsKey = "urls";
    private const string KestrelDefault = "http://localhost:5000";

    /// <summary>Every address in <paramref name="configuration"/>.</summary>
    public static IReadOnlyList<BindingAddress> Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var endpoints = configuration.GetSection(EndpointsSection).GetChildren()
            .Select(endpoint => endpoint[UrlKey])
            .OfType<string>()
            .Where(url => url.Length > 0)
            .ToList();
        if (endpoints.Count == 0)
        {
            var given = configuration[UrlsKey];
            endpoints = (string.IsNullOrWhiteSpace(given) ? KestrelDefault : given)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        return endpoints.Select(BindingAddress.Parse).ToList();
    }

    /// <summary>
    /// Throws when no address in <paramref name="configuration"/> is on <paramref name="port"/>, the port the scripts
    /// switch and watch: DevKit would then answer somewhere nobody opens it, while that port stays dark.
    /// </summary>
    public static void EnsureServes(IConfiguration configuration, int port)
    {
        var addresses = Read(configuration);
        if (!addresses.Any(address => address.Port == port))
        {
            throw new InvalidOperationException(
                $"DevKit would listen on {string.Join(", ", addresses)}, but not on port {port}, which DEVKIT_PORT names. " +
                $"Make the port in the \"Kestrel\" section of {DevKitMachineConfig.FilePath} and DEVKIT_PORT the same.");
        }
    }

    /// <summary>
    /// The first plain http address other machines can reach, where sign-ins and settings would cross the network
    /// unencrypted; null when every address is https or this machine's own.
    /// </summary>
    public static BindingAddress? PlainHttpForOthers(IConfiguration configuration) =>
        Read(configuration).FirstOrDefault(address => !address.IsUnixPipe
                                                      && address.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                                                      && !IsThisMachineOnly(address.Host));

    private static bool IsThisMachineOnly(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}
