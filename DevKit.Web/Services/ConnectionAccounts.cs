using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Win32.SafeHandles;

namespace DevKit.Web.Services;

/// <summary>
/// Which Windows account opened a connection that came from this machine. Local mode's key travels in a cookie, and a
/// browser sends a localhost cookie to every port of localhost, so a program another account runs on this machine could
/// be handed a copy. Asking who opened the connection makes that copy useless to it.
/// </summary>
public interface IConnectionAccounts
{
    /// <summary>
    /// Whether the connection <paramref name="context"/> came in on was opened by a program that runs as the Windows
    /// account DevKit runs as. False when that cannot be told.
    /// </summary>
    bool IsThisAccount(HttpContext context);
}

/// <summary>Where Windows cannot be asked (not Windows): every connection from this machine counts as this account's.</summary>
internal sealed class UncheckedConnectionAccounts : IConnectionAccounts
{
    public bool IsThisAccount(HttpContext context) => true;
}

/// <summary>
/// Asks Windows which program holds the other end of a loopback connection (its TCP table) and which account that program
/// runs as (its token). The answer is kept with the connection, so it is asked once per connection, not per request.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsConnectionAccounts : IConnectionAccounts
{
    private static readonly object AnswerKey = new();
    private readonly SecurityIdentifier _account;

    /// <summary>Connections of programs that run as the account DevKit runs as.</summary>
    public WindowsConnectionAccounts()
        : this(CurrentAccount())
    {
    }

    /// <summary>Connections of programs that run as <paramref name="account"/>, for tests.</summary>
    internal WindowsConnectionAccounts(SecurityIdentifier account)
    {
        _account = account;
    }

    public bool IsThisAccount(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var items = context.Features.Get<IConnectionItemsFeature>()?.Items;
        if (items is not null && items.TryGetValue(AnswerKey, out var kept) && kept is bool known)
        {
            return known;
        }

        var answer = Ask(context.Connection);
        if (items is not null)
        {
            items[AnswerKey] = answer;
        }

        return answer;
    }

    private bool Ask(ConnectionInfo connection)
    {
        if (connection.RemoteIpAddress is not { } client || connection.LocalIpAddress is not { } server
            || TcpOwners.ProgramAt(new IPEndPoint(client, connection.RemotePort), new IPEndPoint(server, connection.LocalPort)) is not { } program)
        {
            return false;
        }

        return ProcessAccounts.Of(program) is { } account && account.Equals(_account);
    }

    private static SecurityIdentifier CurrentAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("The Windows account DevKit runs as has no security identifier.");
    }
}

/// <summary>Which program holds one end of a TCP connection on this machine, read from Windows' TCP tables.</summary>
[SupportedOSPlatform("windows")]
internal static class TcpOwners
{
    private const int AddressFamilyV4 = 2;
    private const int AddressFamilyV6 = 23;
    private const int ConnectionsWithOwner = 4;
    private const int Done = 0;
    private const int BufferTooSmall = 122;
    private const int MaxTries = 4;

    // MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, process id (4 bytes each).
    private const int RowV4Bytes = 24;

    // MIB_TCP6ROW_OWNER_PID: local address (16), scope, port, remote address (16), scope, port, state, process id.
    private const int RowV6Bytes = 56;
    private const int CountBytes = 4;

    /// <summary>
    /// The process id of the program whose socket has <paramref name="client"/> as its own end and <paramref name="server"/>
    /// as the other: the program that connected to DevKit. Null when no such connection is open now.
    /// </summary>
    public static int? ProgramAt(IPEndPoint client, IPEndPoint server)
    {
        var wanted = (Client: Normal(client), Server: Normal(server));
        return Rows(AddressFamilyV4).Concat(Rows(AddressFamilyV6))
            .Where(row => row.Local.Equals(wanted.Client) && row.Remote.Equals(wanted.Server))
            .Select(row => (int?)row.ProcessId)
            .FirstOrDefault();
    }

    /// <summary>An IPv4 address that reached a socket listening on every address arrives as ::ffff:a.b.c.d.</summary>
    private static IPEndPoint Normal(IPEndPoint endpoint) =>
        endpoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint;

    private static List<(IPEndPoint Local, IPEndPoint Remote, uint ProcessId)> Rows(int family)
    {
        var table = Table(family);
        var rows = new List<(IPEndPoint, IPEndPoint, uint)>();
        if (table.Length < CountBytes)
        {
            return rows;
        }

        var span = table.AsSpan();
        var count = BinaryPrimitives.ReadInt32LittleEndian(span);
        var size = family == AddressFamilyV4 ? RowV4Bytes : RowV6Bytes;
        for (var index = 0; index < count && CountBytes + ((index + 1) * size) <= table.Length; index++)
        {
            var row = span.Slice(CountBytes + (index * size), size);
            rows.Add(family == AddressFamilyV4 ? RowV4(row) : RowV6(row));
        }

        return rows;
    }

    private static (IPEndPoint, IPEndPoint, uint) RowV4(ReadOnlySpan<byte> row) => (
        new IPEndPoint(new IPAddress(row.Slice(4, 4)), Port(row.Slice(8, 4))),
        new IPEndPoint(new IPAddress(row.Slice(12, 4)), Port(row.Slice(16, 4))),
        BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(20, 4)));

    private static (IPEndPoint, IPEndPoint, uint) RowV6(ReadOnlySpan<byte> row) => (
        new IPEndPoint(new IPAddress(row.Slice(0, 16)), Port(row.Slice(20, 4))),
        new IPEndPoint(new IPAddress(row.Slice(24, 16)), Port(row.Slice(44, 4))),
        BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(52, 4)));

    // The port is in network byte order in the first two bytes of its field.
    private static int Port(ReadOnlySpan<byte> field) => BinaryPrimitives.ReadUInt16BigEndian(field);

    /// <summary>
    /// The table of one address family, asked again with a larger buffer when connections opened meanwhile; empty when
    /// Windows does not give it.
    /// </summary>
    private static byte[] Table(int family)
    {
        var size = 0;
        for (var tries = 0; tries < MaxTries; tries++)
        {
            var buffer = new byte[size];
            var status = GetExtendedTcpTable(buffer, ref size, sorted: false, family, ConnectionsWithOwner, 0);
            if (status == Done)
            {
                return buffer;
            }

            if (status != BufferTooSmall)
            {
                throw new SocketException(status);
            }
        }

        throw new SocketException(BufferTooSmall);
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetExtendedTcpTable(byte[] table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sorted, int family, int tableClass, int reserved);
}

/// <summary>Which Windows account a process runs as, read from its token without anything more than querying it.</summary>
[SupportedOSPlatform("windows")]
internal static class ProcessAccounts
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    /// <summary>The account process <paramref name="processId"/> runs as; null when this account may not ask, or it has ended.</summary>
    public static SecurityIdentifier? Of(int processId)
    {
        using var process = OpenProcess(QueryLimitedInformation, inheritHandle: false, processId);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token))
        {
            return null;
        }

        using (token)
        {
            return WindowsTokens.User(token);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
