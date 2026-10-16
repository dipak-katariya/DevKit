using System.Security.Claims;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.JSInterop;

namespace DevKit.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// A browser's localStorage behind the JS calls ProtectedLocalStorage makes. Several pages can share one, as the
/// accounts signed in to one browser do.
/// </summary>
internal sealed class FakeBrowserStorage : IJSRuntime
{
    public Dictionary<string, string> Items { get; } = new(StringComparer.Ordinal);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        var key = (string)args![0]!;
        switch (identifier)
        {
            case "localStorage.getItem":
                return new ValueTask<TValue>((TValue)(object?)(Items.TryGetValue(key, out var value) ? value : null)!);
            case "localStorage.setItem":
                Items[key] = (string)args[1]!;
                return new ValueTask<TValue>(default(TValue)!);
            case "localStorage.removeItem":
                Items.Remove(key);
                return new ValueTask<TValue>(default(TValue)!);
            default:
                throw new InvalidOperationException($"Unexpected browser call {identifier}.");
        }
    }
}

/// <summary>Settings text kept in memory, as an earlier DevKit wrote it into a browser.</summary>
internal sealed class MemorySettingsStore : ISettingsStore
{
    public string? Text { get; private set; }

    public string Location => "memory";

    public string? ReadText() => Text;

    public string? ReadBackupText() => null;

    public void Write(string text) => Text = text;

    public void DeleteBackup()
    {
        // Memory keeps no earlier version.
    }

    public void SetAsideCorrupt()
    {
        // Nothing to set aside in memory.
    }
}

/// <summary>Windows's password check, answered from a table, counting how often it was asked.</summary>
internal sealed class FakeCredentialChecker : IWindowsCredentialChecker
{
    private readonly Dictionary<string, (string Password, SignedInUser User)> _accounts = new(StringComparer.OrdinalIgnoreCase);

    public int Calls { get; private set; }

    public void Add(string userName, string password, SignedInUser user) => _accounts[AccountKey(userName)] = (password, user);

    public CredentialCheck Check(string userName, string password)
    {
        Calls++;
        return _accounts.TryGetValue(AccountKey(userName), out var account) && account.Password == password
            ? CredentialCheck.Accepted(account.User)
            : CredentialCheck.Refused("Windows did not accept this username and password.", "wrong username or password");
    }

    public string AccountKey(string userName)
    {
        var typed = userName.Trim();
        return (typed.Contains('\\', StringComparison.Ordinal) ? typed : $"MT\\{typed}").ToUpperInvariant();
    }
}

/// <summary>What a Kestrel connection keeps between the requests sent over it.</summary>
internal sealed class FakeConnectionItems : IConnectionItemsFeature
{
    public IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();
}

/// <summary>A page's sign-in state as Blazor hands it to components.</summary>
internal sealed class FixedAuthenticationState : AuthenticationStateProvider
{
    private readonly ClaimsPrincipal _user;

    public FixedAuthenticationState(ClaimsPrincipal user)
    {
        _user = user;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(_user));
}
