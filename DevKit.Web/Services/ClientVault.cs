using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace DevKit.Web.Services;

/// <summary>
/// The key of one person's settings, of their own, and the same key locked by the <see cref="IKeyLock"/>. The locked
/// key travels with the settings it seals, so whichever DevKit server opens them needs nothing copied from another.
/// </summary>
internal sealed class PersonKey
{
    internal PersonKey(byte[] value, string locked)
    {
        Value = value;
        Locked = locked;
    }

    internal byte[] Value { get; }

    /// <summary>The locked key, as the sealed text carries it.</summary>
    internal string Locked { get; }
}

/// <summary>A person's settings text, opened, and their key, for the saves that follow.</summary>
internal sealed record OpenedSettings(string Text, PersonKey Key);

/// <summary>
/// Shared mode keeps each person's settings, PAT included, only on their own PC. They are sealed here with AES-256-GCM
/// under a key of the person's own, bound to their Windows account, and kept by their browser in cookies of this
/// machine's DNS domain. Their key goes with them, locked by the domain to the account DevKit runs as, so every DevKit
/// server in the domain running as that account opens them, and DevKit can move to another server with nothing copied
/// and nobody entering anything again. The server keeps no key and no settings. The cookies go only to
/// <see cref="Address"/>, so other sites in the domain never receive them, and are HttpOnly, so a page's scripts only
/// carry the sealed text between the circuit and this address. A browser that reaches DevKit by a name outside the
/// domain (an IP address, the short machine name) keeps them for that name alone.
/// </summary>
internal sealed class ClientVault
{
    public const string Address = "/devkit-vault";

    // A cookie's name and value may hold 4,096 bytes: each piece stays well below that, and all of them below Kestrel's
    // 32 KB of request headers.
    internal const int PieceChars = 3_800;
    internal const int MaxPieces = 6;
    internal const int MaxSealedChars = PieceChars * MaxPieces;

    /// <summary>How long a browser keeps the cookies after a page last read or wrote them; browsers keep none longer.</summary>
    internal static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(400);

    private const string FormatVersion = "v2";
    private const char PartSeparator = '.';
    private const int PartCount = 4;
    private const int KeyBytes = 32;
    private const string BoundTo = "DevKit.ClientVault.v1|";
    private const string CookiePrefix = "devkit-vault-";
    private const int CookieUserHashBytes = 8;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int MaxTextBytes = 1 << 20;
    private const int ReadBufferBytes = 8_192;

    private readonly IKeyLock _keyLock;
    private readonly string _domain;

    /// <param name="keyLock">Locks each person's key to the account DevKit runs as.</param>
    /// <param name="machineDomain">This machine's DNS domain, whose names share the cookies; empty when it has none.</param>
    public ClientVault(IKeyLock keyLock, string machineDomain)
    {
        ArgumentNullException.ThrowIfNull(keyLock);
        ArgumentNullException.ThrowIfNull(machineDomain);
        _keyLock = keyLock;
        _domain = machineDomain.Trim().TrimEnd('.');
    }

    /// <summary>A new key for one person's settings, already locked. Throws <see cref="IOException"/> when it cannot be locked now.</summary>
    public PersonKey NewKey()
    {
        var value = RandomNumberGenerator.GetBytes(KeyBytes);
        return new PersonKey(value, Base64Url.EncodeToString(_keyLock.Lock(value)));
    }

    /// <summary>The settings text sealed for <paramref name="user"/> under their <paramref name="key"/>. Throws <see cref="IOException"/> when it is too large for a browser to keep.</summary>
    public string Seal(SignedInUser user, PersonKey key, string text)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(key);
        var plain = Compress(text);
        var sealedBytes = new byte[NonceBytes + plain.Length + TagBytes];
        var nonce = sealedBytes.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(key.Value, TagBytes))
        {
            aes.Encrypt(nonce, plain, sealedBytes.AsSpan(NonceBytes, plain.Length), sealedBytes.AsSpan(NonceBytes + plain.Length), BoundData(user));
        }

        var sealedText = string.Join(PartSeparator, FormatVersion, _keyLock.Account, key.Locked, Base64Url.EncodeToString(sealedBytes));
        if (sealedText.Length > MaxSealedChars)
        {
            throw new IOException($"The settings are too large for a browser to keep: {sealedText.Length} characters sealed, at most {MaxSealedChars}.");
        }

        return sealedText;
    }

    /// <summary>
    /// The settings text <paramref name="sealedText"/> holds for <paramref name="user"/>, with their key. Throws
    /// <see cref="CryptographicException"/> when it was sealed by DevKit running as another account, or for another
    /// person, or was changed or damaged; and <see cref="IOException"/> when the key cannot be opened now, so that
    /// nothing is saved over settings that may open in a minute.
    /// </summary>
    public OpenedSettings Open(SignedInUser user, string sealedText)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!TrySplit(sealedText, out var parts))
        {
            throw new CryptographicException("The settings kept in the browser are not in the form DevKit seals them in.");
        }

        if (parts.Account != _keyLock.Account)
        {
            throw new CryptographicException(
                $"The settings kept in the browser were sealed by DevKit running as another Windows account ({parts.Account}); this DevKit runs as {_keyLock.Account}.");
        }

        var key = new PersonKey(_keyLock.Unlock(Base64Url.DecodeFromChars(parts.LockedKey)), parts.LockedKey);
        if (key.Value.Length != KeyBytes)
        {
            throw new CryptographicException("The key kept with the settings is not a settings key.");
        }

        var sealedBytes = Base64Url.DecodeFromChars(parts.Payload);
        var plain = new byte[sealedBytes.Length - NonceBytes - TagBytes];
        using (var aes = new AesGcm(key.Value, TagBytes))
        {
            aes.Decrypt(sealedBytes.AsSpan(0, NonceBytes), sealedBytes.AsSpan(NonceBytes, plain.Length),
                sealedBytes.AsSpan(NonceBytes + plain.Length), plain, BoundData(user));
        }

        return new OpenedSettings(Decompress(plain), key);
    }

    /// <summary>Whether <paramref name="sealedText"/> has the shape <see cref="Seal"/> gives it on a DevKit running as this account; checked before a browser keeps it.</summary>
    public bool IsWellFormed([NotNullWhen(true)] string? sealedText) =>
        sealedText is { Length: <= MaxSealedChars }
        && TrySplit(sealedText, out var parts)
        && parts.Account == _keyLock.Account;

    /// <summary>The sealed settings the browser sent for <paramref name="user"/>, or null when it keeps none.</summary>
    public static string? ReadFrom(IRequestCookieCollection cookies, SignedInUser user)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        var name = CookieNameFor(user);
        var sealedText = new StringBuilder();
        for (var piece = 0; piece < MaxPieces && cookies.TryGetValue(name + piece, out var value); piece++)
        {
            sealedText.Append(value);
        }

        return sealedText.Length == 0 ? null : sealedText.ToString();
    }

    /// <summary>
    /// Has the browser keep <paramref name="sealedText"/> for <paramref name="user"/> for another
    /// <see cref="CookieLifetime"/>, in as many cookies as it takes, and drop pieces left from longer settings.
    /// </summary>
    public void WriteTo(HttpContext context, SignedInUser user, string sealedText)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sealedText);
        var name = CookieNameFor(user);
        var options = CookieOptionsFor(context);
        for (var piece = 0; piece < MaxPieces; piece++)
        {
            var start = piece * PieceChars;
            if (start < sealedText.Length)
            {
                context.Response.Cookies.Append(name + piece, sealedText.Substring(start, Math.Min(PieceChars, sealedText.Length - start)), options);
            }
            else if (context.Request.Cookies.ContainsKey(name + piece))
            {
                context.Response.Cookies.Delete(name + piece, options);
            }
        }
    }

    /// <summary>
    /// This machine's DNS domain when the browser reached DevKit by a name in it, so that every DevKit server there
    /// shares the cookies; null, for this name alone, otherwise.
    /// </summary>
    internal string? CookieDomainFor(string host) =>
        _domain.Length > 0 && host.EndsWith("." + _domain, StringComparison.OrdinalIgnoreCase) ? _domain : null;

    /// <summary>The first part of each cookie's name, followed by its piece number: a hash, so no name or identifier shows.</summary>
    internal static string CookieNameFor(SignedInUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return CookiePrefix + Convert.ToHexString(user.IdHash(), 0, CookieUserHashBytes).ToLowerInvariant() + "-";
    }

    private CookieOptions CookieOptionsFor(HttpContext context) => new()
    {
        Domain = CookieDomainFor(context.Request.Host.Host),
        Path = Address,
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        MaxAge = CookieLifetime,
        IsEssential = true
    };

    /// <summary>Splits sealed text into its parts, when it has the shape <see cref="Seal"/> gives it.</summary>
    private static bool TrySplit(string sealedText, out SealedParts parts)
    {
        var split = sealedText.Split(PartSeparator);
        var wellFormed = split.Length == PartCount
            && split[0] == FormatVersion
            && Base64Url.IsValid(split[2], out var lockedLength) && lockedLength > 0
            && Base64Url.IsValid(split[3], out var payloadLength) && payloadLength >= NonceBytes + TagBytes;
        parts = wellFormed ? new SealedParts(split[1], split[2], split[3]) : new SealedParts("", "", "");
        return wellFormed;
    }

    /// <summary>Sealed text's parts: the hash of the account its key is locked to, that locked key, and the sealed settings.</summary>
    private readonly record struct SealedParts(string Account, string LockedKey, string Payload);

    /// <summary>Who the text is sealed for: text sealed for one account does not open as another's.</summary>
    private static byte[] BoundData(SignedInUser user) => Encoding.UTF8.GetBytes(BoundTo + user.Id);

    private static byte[] Compress(string text)
    {
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(Encoding.UTF8.GetBytes(text));
        }

        return compressed.ToArray();
    }

    // Only text this server sealed gets here, since it decrypted; the limit still keeps a mistake from filling memory.
    private static string Decompress(byte[] compressed)
    {
        using var brotli = new BrotliStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var text = new MemoryStream();
        var buffer = new byte[ReadBufferBytes];
        int read;
        while ((read = brotli.Read(buffer)) > 0)
        {
            if (text.Length + read > MaxTextBytes)
            {
                throw new CryptographicException($"The settings kept in the browser open to more than {MaxTextBytes} bytes.");
            }

            text.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length);
    }
}

/// <summary>The address a page's script reads and writes the signed-in person's sealed settings through (<see cref="ClientVault"/>).</summary>
internal static class ClientVaultEndpoints
{
    // Room for the JSON around the sealed text.
    private const int RequestOverheadBytes = 1_024;

    public static void MapClientVaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet(ClientVault.Address, Read);
        app.MapPost(ClientVault.Address, Write)
            .WithMetadata(new RequestSizeLimitAttribute(ClientVault.MaxSealedChars + RequestOverheadBytes));
    }

    /// <summary>The sealed settings this browser keeps for the person signed in, which it then keeps for another <see cref="ClientVault.CookieLifetime"/>.</summary>
    internal static IResult Read(HttpContext context, ClientVault vault, OwnerAccount owner)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (SignedInUser.From(context.User, owner.Id) is not { } user)
        {
            return Results.Unauthorized();
        }

        context.Response.Headers.CacheControl = "no-store";
        if (ClientVault.ReadFrom(context.Request.Cookies, user) is not { } sealedText)
        {
            return Results.NoContent();
        }

        vault.WriteTo(context, user, sealedText);
        return Results.Text(sealedText, MediaTypeNames.Text.Plain);
    }

    /// <summary>Has this browser keep sealed settings for the person signed in; anything but text this server sealed is refused.</summary>
    internal static IResult Write(HttpContext context, ClientVault vault, OwnerAccount owner, [FromBody] VaultWrite body)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (SignedInUser.From(context.User, owner.Id) is not { } user)
        {
            return Results.Unauthorized();
        }

        context.Response.Headers.CacheControl = "no-store";
        if (!vault.IsWellFormed(body.Sealed))
        {
            return Results.BadRequest();
        }

        vault.WriteTo(context, user, body.Sealed);
        return Results.NoContent();
    }
}

/// <summary>What a page posts to <see cref="ClientVault.Address"/>.</summary>
internal sealed record VaultWrite(string? Sealed);
