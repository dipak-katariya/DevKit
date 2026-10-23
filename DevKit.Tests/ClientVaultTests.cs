using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using DevKit.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The vault seals a person's settings for their Windows account under a key of their own, which the domain locks to
/// the account DevKit runs as, so any DevKit server running as that account opens them, with nothing copied to it, and
/// only as that person. A key the domain cannot open now is never mistaken for settings that cannot be read. The
/// browser keeps them in HttpOnly cookies for the whole domain, sent only to the vault's own address, which hands them
/// only to the person signed in and keeps only text in the shape this DevKit seals it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClientVaultTests
{
    private const string Domain = "example.test";
    private const string Settings = """{ "TeamName": "TEAM-A", "Tfs": { "Pat": "alice-test-pat-1c4e" } }""";
    private const string RunsAs = "S-1-5-21-1-2-3-500";
    private const string AnotherAccount = "S-1-5-21-1-2-3-600";

    private static readonly SignedInUser Alice = new("S-1-5-21-1-2-3-1001", @"MT\alice", IsOwner: false);
    private static readonly SignedInUser Bob = new("S-1-5-21-1-2-3-1002", @"MT\bob", IsOwner: false);
    private static readonly OwnerAccount RunsDevKit = new(RunsAs);

    private readonly FakeKeyLock _keyLock = new(RunsAs);
    private readonly ClientVault _vault;

    public ClientVaultTests()
    {
        _vault = new ClientVault(_keyLock, Domain);
    }

    [Fact]
    public void Sealed_settings_open_again_for_the_same_account_with_the_key_they_were_sealed_under()
    {
        var key = _vault.NewKey();

        var opened = _vault.Open(Alice, _vault.Seal(Alice, key, Settings));

        Assert.Equal(Settings, opened.Text);
        Assert.Equal(key.Value, opened.Key.Value);
        Assert.Equal(key.Locked, opened.Key.Locked);
    }

    [Fact]
    public void The_same_settings_seal_differently_each_time()
    {
        var key = _vault.NewKey();

        Assert.NotEqual(_vault.Seal(Alice, key, Settings), _vault.Seal(Alice, key, Settings));
    }

    [Fact]
    public void Every_new_key_is_a_different_one()
    {
        var first = _vault.NewKey();
        var second = _vault.NewKey();

        Assert.NotEqual(first.Value, second.Value);
        Assert.NotEqual(first.Locked, second.Locked);
    }

    [Fact]
    public void Settings_sealed_for_one_person_do_not_open_as_another()
    {
        var sealedText = Seal(Alice, Settings);

        Assert.ThrowsAny<CryptographicException>(() => _vault.Open(Bob, sealedText));
    }

    [Fact]
    public void Another_DevKit_server_running_as_the_same_account_opens_them_with_nothing_copied()
    {
        var newServer = new ClientVault(new FakeKeyLock(RunsAs), Domain);

        Assert.Equal(Settings, newServer.Open(Alice, Seal(Alice, Settings)).Text);
    }

    [Fact]
    public void Settings_sealed_by_DevKit_running_as_another_account_do_not_open_and_say_so()
    {
        var other = new ClientVault(new FakeKeyLock(AnotherAccount), Domain);
        var sealedText = other.Seal(Alice, other.NewKey(), Settings);

        var refused = Assert.ThrowsAny<CryptographicException>(() => _vault.Open(Alice, sealedText));

        Assert.Contains("another Windows account", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AnotherAccount, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_the_domain_cannot_open_now_is_not_taken_for_unreadable_settings()
    {
        var sealedText = Seal(Alice, Settings);
        _keyLock.Unavailable = true;

        Assert.Throws<IOException>(() => _vault.Open(Alice, sealedText));
        Assert.Throws<IOException>(() => _vault.NewKey());
    }

    [Theory]
    [InlineData("flip")]
    [InlineData("key")]
    [InlineData("cut")]
    [InlineData("v1")]
    [InlineData("junk")]
    public void Changed_or_damaged_settings_do_not_open(string damage)
    {
        var sealedText = Seal(Alice, Settings);
        var parts = sealedText.Split('.');
        var damaged = damage switch
        {
            "flip" => sealedText[..^5] + (sealedText[^5] == 'A' ? 'B' : 'A') + sealedText[^4..],
            "key" => string.Join('.', parts[0], parts[1], Flipped(parts[2]), parts[3]),
            "cut" => sealedText[..^20],
            "v1" => "v1" + sealedText[2..],
            _ => "not sealed at all"
        };

        Assert.ThrowsAny<CryptographicException>(() => _vault.Open(Alice, damaged));
    }

    [Fact]
    public void Settings_too_large_for_a_browser_are_refused()
    {
        var incompressible = Convert.ToBase64String(RandomNumberGenerator.GetBytes(ClientVault.MaxSealedChars));

        Assert.Throws<IOException>(() => Seal(Alice, incompressible));
    }

    [Fact]
    public void Only_text_in_the_shape_this_DevKit_seals_it_is_kept()
    {
        var sealedText = Seal(Alice, Settings);
        var other = new ClientVault(new FakeKeyLock(AnotherAccount), Domain);
        var anotherAccounts = other.Seal(Alice, other.NewKey(), Settings);
        var parts = sealedText.Split('.');

        Assert.True(_vault.IsWellFormed(sealedText));
        Assert.False(_vault.IsWellFormed(null));
        Assert.False(_vault.IsWellFormed(anotherAccounts));
        Assert.False(_vault.IsWellFormed("v1.12345678.***"));
        Assert.False(_vault.IsWellFormed(string.Join('.', parts[0], parts[1], "", parts[3])));
        Assert.False(_vault.IsWellFormed(string.Join('.', parts[0], parts[1], parts[2], "AAAA")));
        Assert.False(_vault.IsWellFormed(sealedText + new string('A', ClientVault.MaxSealedChars)));
    }

    [Fact]
    public void The_browser_keeps_them_in_pieces_for_the_whole_domain_sent_only_to_the_vault_address()
    {
        var context = Request("devkit-a.example.test");
        var sealedText = LongSealedText();

        _vault.WriteTo(context, Alice, sealedText);

        var cookies = SetCookies(context);
        Assert.Equal(3, cookies.Count);
        Assert.All(cookies, cookie =>
        {
            Assert.StartsWith(ClientVault.CookieNameFor(Alice), cookie.Name.Value, StringComparison.Ordinal);
            Assert.True(cookie.Value.Length <= ClientVault.PieceChars);
            Assert.Equal(Domain, cookie.Domain.Value);
            Assert.Equal(ClientVault.Address, cookie.Path.Value);
            Assert.True(cookie.HttpOnly);
            Assert.True(cookie.Secure);
            Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
            Assert.Equal(ClientVault.CookieLifetime, cookie.MaxAge);
        });

        var next = Request("devkit-b.example.test", cookies);
        Assert.Equal(sealedText, _vault.ReadFrom(next.Request.Cookies, Alice));
        Assert.Null(_vault.ReadFrom(next.Request.Cookies, Bob));
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("devbox")]
    [InlineData("devkit.example.test.evil.test")]
    [InlineData("notexample.test")]
    public void A_name_outside_the_domain_keeps_them_for_that_name_alone(string host)
    {
        var context = Request(host);

        _vault.WriteTo(context, Alice, Seal(Alice, Settings));

        Assert.All(SetCookies(context), cookie => Assert.False(cookie.Domain.HasValue));
    }

    [Fact]
    public void Pieces_left_from_longer_settings_are_dropped()
    {
        var first = Request("devkit-a.example.test");
        _vault.WriteTo(first, Alice, LongSealedText());
        var context = Request("devkit-a.example.test", SetCookies(first));

        _vault.WriteTo(context, Alice, Seal(Alice, Settings));

        var cookies = SetCookies(context);
        Assert.Equal(3, cookies.Count);
        Assert.Equal(2, cookies.Count(c => c.Expires < DateTimeOffset.UtcNow && c.MaxAge is null));
    }

    [Fact]
    public void The_cookies_name_nobody()
    {
        var name = ClientVault.CookieNameFor(Alice);

        Assert.DoesNotContain(Alice.Id, name, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", name, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(name, ClientVault.CookieNameFor(Bob));
    }

    [Fact]
    public void Reading_with_nothing_kept_answers_no_content_and_is_never_cached()
    {
        var context = SignedIn(Request("devkit-a.example.test"), Alice);

        Assert.IsType<NoContent>(ClientVaultEndpoints.Read(context, _vault, RunsDevKit));
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void Reading_answers_the_sealed_text_and_keeps_it_longer()
    {
        var sealedText = Seal(Alice, Settings);
        var kept = Request("devkit-a.example.test");
        _vault.WriteTo(kept, Alice, sealedText);
        var context = SignedIn(Request("devkit-b.example.test", SetCookies(kept)), Alice);

        var answer = Assert.IsType<ContentHttpResult>(ClientVaultEndpoints.Read(context, _vault, RunsDevKit));

        Assert.Equal(sealedText, answer.ResponseContent);
        Assert.Single(SetCookies(context));
    }

    [Fact]
    public void Writing_keeps_only_text_this_server_sealed_for_the_person_signed_in()
    {
        var refused = SignedIn(Request("devkit-a.example.test"), Alice);
        Assert.IsType<BadRequest>(ClientVaultEndpoints.Write(refused, _vault, RunsDevKit, new VaultWrite("v1.00000000.AAAA")));
        Assert.Empty(SetCookies(refused));

        var context = SignedIn(Request("devkit-a.example.test"), Alice);
        Assert.IsType<NoContent>(ClientVaultEndpoints.Write(context, _vault, RunsDevKit, new VaultWrite(Seal(Alice, Settings))));
        Assert.StartsWith(ClientVault.CookieNameFor(Alice), Assert.Single(SetCookies(context)).Name.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Nobody_signed_in_reads_or_keeps_nothing()
    {
        var context = Request("devkit-a.example.test");

        Assert.IsType<UnauthorizedHttpResult>(ClientVaultEndpoints.Read(context, _vault, RunsDevKit));
        Assert.IsType<UnauthorizedHttpResult>(ClientVaultEndpoints.Write(context, _vault, RunsDevKit, new VaultWrite(Seal(Alice, Settings))));
        Assert.Empty(SetCookies(context));
    }

    /// <summary><paramref name="text"/> sealed for <paramref name="user"/> under a new key of theirs.</summary>
    private string Seal(SignedInUser user, string text) => _vault.Seal(user, _vault.NewKey(), text);

    /// <summary><paramref name="part"/> with one character in the middle changed, still in base64url.</summary>
    private static string Flipped(string part)
    {
        var middle = part.Length / 2;
        return part[..middle] + (part[middle] == 'A' ? 'B' : 'A') + part[(middle + 1)..];
    }

    /// <summary>Sealed text that takes three cookies: random settings barely compress.</summary>
    private string LongSealedText() =>
        Seal(Alice, Convert.ToBase64String(RandomNumberGenerator.GetBytes(ClientVault.PieceChars * 2)));

    /// <summary>An https request to <paramref name="host"/>, carrying the cookies a browser kept from an earlier answer.</summary>
    private static DefaultHttpContext Request(string host, IEnumerable<SetCookieHeaderValue>? kept = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host, 5850);
        context.Request.Path = ClientVault.Address;
        var cookies = (kept ?? []).Where(c => c.Expires is null || c.Expires > DateTimeOffset.UtcNow).Select(c => $"{c.Name}={c.Value}");
        context.Request.Headers.Cookie = string.Join("; ", cookies);
        return context;
    }

    private static DefaultHttpContext SignedIn(DefaultHttpContext context, SignedInUser user)
    {
        context.User = new SessionRegistry(new ManualClock()).Start(user, "windows", TimeSpan.FromDays(5), IPAddress.Loopback).Principal;
        return context;
    }

    private static IList<SetCookieHeaderValue> SetCookies(HttpContext context) =>
        SetCookieHeaderValue.ParseList(context.Response.Headers.SetCookie.ToArray()!);
}
