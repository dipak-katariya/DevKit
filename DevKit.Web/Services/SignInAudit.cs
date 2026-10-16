using System.Globalization;
using System.Net;
using System.Text;

namespace DevKit.Web.Services;

/// <summary>
/// The server's audit log of who signed in to shared DevKit, from which machine, and of every failed attempt and
/// sign-out: one tab-separated line per event, in a file per month. It never holds a password or a token.
/// </summary>
internal sealed class SignInAudit : IDisposable
{
    private const string FileFormat = "signin-{0:yyyy-MM}.log";
    private const int MaxFieldLength = 200;
    private static readonly TimeSpan MachineLookupTimeout = TimeSpan.FromSeconds(2);

    // No byte-order mark, so the first line reads like every other one in tools that split on tabs.
    private static readonly Encoding PlainUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _folder;
    private readonly ILogger<SignInAudit> _logger;
    private readonly SemaphoreSlim _writing = new(1, 1);

    public SignInAudit(string folder, ILogger<SignInAudit> logger)
    {
        _folder = folder;
        _logger = logger;
    }

    /// <summary>
    /// Records one event as "user@machine". A line that cannot be written is logged at Error and the sign-in goes on:
    /// the audit must not lock people out.
    /// </summary>
    /// <param name="event">What happened, e.g. "sign-in", "failed", "sign-out".</param>
    /// <param name="method">How: "windows" (single sign-on) or "password".</param>
    /// <param name="user">The account, or what was typed for a failed attempt.</param>
    /// <param name="detail">Why an attempt failed; empty otherwise.</param>
    public async Task RecordAsync(HttpContext context, string @event, string method, string user, string detail = "")
    {
        ArgumentNullException.ThrowIfNull(context);
        var address = LocalAccessGate.ClientAddress(context);
        var line = string.Join('\t',
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Clean(@event), Clean(method), $"{Clean(user)}@{Clean(await MachineNameAsync(address))}", Clean(address?.ToString() ?? ""), Clean(detail));
        // Who and from where go to the audit file only; the general log stays free of account names.
        _logger.LogInformation("Sign-in audit: {Event} by {Method}", @event, method);

        await _writing.WaitAsync();
        try
        {
            Directory.CreateDirectory(_folder);
            await File.AppendAllTextAsync(
                Path.Combine(_folder, string.Format(CultureInfo.InvariantCulture, FileFormat, DateTime.UtcNow)),
                line + Environment.NewLine,
                PlainUtf8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The sign-in audit line for {Event} could not be written", @event);
        }
        finally
        {
            _writing.Release();
        }
    }

    public void Dispose() => _writing.Dispose();

    /// <summary>The caller's machine name from DNS, or its address when DNS does not answer in time.</summary>
    private async Task<string> MachineNameAsync(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (IPAddress.IsLoopback(address))
        {
            return Environment.MachineName;
        }

        using var timeout = new CancellationTokenSource(MachineLookupTimeout);
        try
        {
            return (await Dns.GetHostEntryAsync(address.ToString(), timeout.Token)).HostName;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException)
        {
            // DNS has no name for the address, or did not answer in time: the address itself is recorded instead.
            _logger.LogError(ex, "DNS gave no machine name for a sign-in address; the audit records the address");
            return address.ToString();
        }
    }

    /// <summary>Typed text kept to one bounded field: no tabs or line breaks can forge another line.</summary>
    internal static string Clean(string text)
    {
        var kept = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return kept.Length <= MaxFieldLength ? kept : kept[..MaxFieldLength];
    }
}
