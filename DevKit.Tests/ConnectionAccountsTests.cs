using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Which Windows account opened a connection from this machine, asked of Windows' own TCP table and the program's token:
/// over real loopback connections this test opens, so the program is this test and the account its own.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConnectionAccountsTests
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task A_connection_this_test_opened_is_this_accounts_and_no_other(string address)
    {
        var loopback = IPAddress.Parse(address);
        using var listener = new TcpListener(loopback, 0);
        listener.Start();
        using var client = new TcpClient(loopback.AddressFamily);
        await client.ConnectAsync(loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();

        var context = ContextFor(accepted.Client);

        Assert.True(new WindowsConnectionAccounts().IsThisAccount(context));
        Assert.False(new WindowsConnectionAccounts(System).IsThisAccount(context));
    }

    // Kestrel on http://*:port takes IPv4 clients over IPv6, so they arrive as ::ffff:127.0.0.1.
    [Fact]
    public async Task An_ipv4_client_of_a_listener_on_every_address_is_found_too()
    {
        using var listener = new TcpListener(IPAddress.IPv6Any, 0);
        listener.Server.DualMode = true;
        listener.Start();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();

        var context = ContextFor(accepted.Client);

        Assert.True(context.Connection.RemoteIpAddress!.IsIPv4MappedToIPv6);
        Assert.True(new WindowsConnectionAccounts().IsThisAccount(context));
    }

    [Fact]
    public void A_connection_that_is_not_open_belongs_to_nobody()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.RemotePort = 1;
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        context.Connection.LocalPort = 2;

        Assert.False(new WindowsConnectionAccounts().IsThisAccount(context));
    }

    [Fact]
    public void A_connection_without_addresses_belongs_to_nobody()
    {
        Assert.False(new WindowsConnectionAccounts().IsThisAccount(new DefaultHttpContext()));
    }

    [Fact]
    public async Task The_answer_is_asked_once_and_kept_with_the_connection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var accepted = await listener.AcceptTcpClientAsync();
        var context = ContextFor(accepted.Client);
        context.Features.Set<IConnectionItemsFeature>(new FakeConnectionItems());
        var accounts = new WindowsConnectionAccounts();

        Assert.True(accounts.IsThisAccount(context));
        client.Dispose();
        accepted.Dispose();

        Assert.True(accounts.IsThisAccount(context));
    }

    private static DefaultHttpContext ContextFor(Socket accepted)
    {
        var remote = (IPEndPoint)accepted.RemoteEndPoint!;
        var local = (IPEndPoint)accepted.LocalEndPoint!;
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote.Address;
        context.Connection.RemotePort = remote.Port;
        context.Connection.LocalIpAddress = local.Address;
        context.Connection.LocalPort = local.Port;
        return context;
    }
}
