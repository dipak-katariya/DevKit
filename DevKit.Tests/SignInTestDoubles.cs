using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
/// A browser behind the JS calls DevKit makes: the localStorage ProtectedLocalStorage uses, and the sealed settings it
/// keeps in DevKit's cookies, as /devkit-vault reads and keeps them for the person signed in. Several pages can share
/// one, as the pages of one browser do.
/// </summary>
internal sealed class FakeBrowserStorage : IJSRuntime
{
    public Dictionary<string, string> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>The sealed settings this browser keeps; null when it keeps none.</summary>
    public string? Vault { get; set; }

    /// <summary>When set, the browser does not keep settings, as one that refuses DevKit's cookies.</summary>
    public bool RefusesVault { get; set; }

    /// <summary>How many saves the browser kept.</summary>
    public int VaultSaves { get; private set; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        switch (identifier)
        {
            case "localStorage.getItem":
                return new ValueTask<TValue>((TValue)(object?)(Items.TryGetValue(Key(args), out var value) ? value : null)!);
            case "localStorage.setItem":
                Items[Key(args)] = (string)args![1]!;
                return new ValueTask<TValue>(default(TValue)!);
            case "localStorage.removeItem":
                Items.Remove(Key(args));
                return new ValueTask<TValue>(default(TValue)!);
            case ClientSettingsStore.LoadFunction:
                return new ValueTask<TValue>((TValue)(object?)Vault!);
            case ClientSettingsStore.SaveFunction:
                if (RefusesVault)
                {
                    throw new JSException("This browser did not keep the settings (HTTP 400).");
                }

                Vault = (string)args![0]!;
                VaultSaves++;
                return new ValueTask<TValue>(default(TValue)!);
            default:
                throw new InvalidOperationException($"Unexpected browser call {identifier}.");
        }
    }

    private static string Key(object?[]? args) => (string)args![0]!;
}

/// <summary>
/// The domain's key lock: a key locked for one account opens for every lock of that account, as on any machine in the
/// domain, and for no other. <see cref="Unavailable"/> stands for a domain controller that does not answer.
/// </summary>
internal sealed class FakeKeyLock : IKeyLock
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[] _domainSecret;

    public FakeKeyLock(string accountId)
    {
        Account = IKeyLock.AccountHash(accountId);
        _domainSecret = SHA256.HashData(Encoding.UTF8.GetBytes("domain|" + accountId));
    }

    public string Account { get; }

    public bool Unavailable { get; set; }

    public byte[] Lock(byte[] key)
    {
        ThrowWhenUnavailable();
        var locked = new byte[NonceBytes + key.Length + TagBytes];
        RandomNumberGenerator.Fill(locked.AsSpan(0, NonceBytes));
        using var aes = new AesGcm(_domainSecret, TagBytes);
        aes.Encrypt(locked.AsSpan(0, NonceBytes), key, locked.AsSpan(NonceBytes, key.Length), locked.AsSpan(NonceBytes + key.Length));
        return locked;
    }

    public byte[] Unlock(byte[] locked)
    {
        ThrowWhenUnavailable();
        if (locked.Length <= NonceBytes + TagBytes)
        {
            throw new CryptographicException("Not a locked key.");
        }

        var key = new byte[locked.Length - NonceBytes - TagBytes];
        using var aes = new AesGcm(_domainSecret, TagBytes);
        aes.Decrypt(locked.AsSpan(0, NonceBytes), locked.AsSpan(NonceBytes, key.Length), locked.AsSpan(NonceBytes + key.Length), key);
        return key;
    }

    private void ThrowWhenUnavailable()
    {
        if (Unavailable)
        {
            throw new IOException("The domain did not answer.");
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
