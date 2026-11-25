using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

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
/// Seals each person's settings, PAT included, for shared mode: AES-256-GCM under a key of the person's own, bound to
/// their Windows account, and that key locked by the domain to the account DevKit runs as. The sealed text is kept on
/// the DevKit server (<see cref="PersonVault"/>), and opens only on a DevKit running as that account, for that person.
/// <para>
/// Sealing keeps the settings secret, but does not show who sealed them: anyone can lock a key to the account DevKit
/// runs as, as anyone can encrypt to a public key. So sealed text is only trusted where DevKit alone can write it, the
/// server's settings folder. Earlier versions kept it in each person's browser, in cookies of this machine's DNS domain
/// that go only to <see cref="Address"/>; those are never read, only cleared (<see cref="ClientVaultEndpoints"/>).
/// </para>
/// </summary>
internal sealed class ClientVault
{
    public const string Address = "/devkit-vault";

    // Earlier versions kept the sealed text in cookies of at most this many pieces, each well below a cookie's 4,096
    // bytes; sealed settings stay within what those held.
    internal const int PieceChars = 3_800;
    internal const int MaxPieces = 6;
    internal const int MaxSealedChars = PieceChars * MaxPieces;

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

    /// <summary>The settings text sealed for <paramref name="user"/> under their <paramref name="key"/>. Throws <see cref="IOException"/> when it is larger than settings ever are.</summary>
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
            throw new IOException($"The settings are too large to keep: {sealedText.Length} characters sealed, at most {MaxSealedChars}.");
        }

        return sealedText;
    }

    /// <summary>
    /// The settings text <paramref name="sealedText"/> holds for <paramref name="user"/>, with their key. Throws
    /// <see cref="CryptographicException"/> when it was sealed by DevKit running as another account, or for another
    /// person, or was changed or damaged; and <see cref="IOException"/> when the key cannot be opened now, so that
    /// nothing is saved over settings that may open in a minute.
    /// </summary>
    /// <param name="known">
    /// The person's key as this page already opened it, used when the text carries the same locked key, so reading
    /// settings again does not ask the domain again.
    /// </param>
    public OpenedSettings Open(SignedInUser user, string sealedText, PersonKey? known = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!TrySplit(sealedText, out var parts))
        {
            throw new CryptographicException("The settings are not in the form DevKit seals them in.");
        }

        if (parts.Account != _keyLock.Account)
        {
            throw new CryptographicException(
                $"The settings were sealed by DevKit running as another Windows account ({parts.Account}); this DevKit runs as {_keyLock.Account}.");
        }

        var key = known is not null && string.Equals(known.Locked, parts.LockedKey, StringComparison.Ordinal)
            ? known
            : new PersonKey(_keyLock.Unlock(Base64Url.DecodeFromChars(parts.LockedKey)), parts.LockedKey);
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

    /// <summary>Whether <paramref name="text"/> has the shape <see cref="Seal"/> gives sealed settings, on any DevKit.</summary>
    internal static bool HasSealedShape([NotNullWhen(true)] string? text) =>
        text is { Length: > 0 and <= MaxSealedChars } && TrySplit(text, out _);

    /// <summary>Has the browser drop every cookie an earlier DevKit kept <paramref name="user"/>'s settings in.</summary>
    public void Clear(HttpContext context, SignedInUser user)
    {
        ArgumentNullException.ThrowIfNull(context);
        var name = CookieNameFor(user);
        var options = CookieOptionsFor(context);
        for (var piece = 0; piece < MaxPieces; piece++)
        {
            if (context.Request.Cookies.ContainsKey(name + piece))
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
    internal static string CookieNameFor(SignedInUser user) => CookiePrefix + PersonTag(user) + "-";

    /// <summary>What names a person's settings wherever they are kept: 16 hex digits of a hash, so no name or identifier shows.</summary>
    internal static string PersonTag(SignedInUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Convert.ToHexString(user.IdHash(), 0, CookieUserHashBytes).ToLowerInvariant();
    }

    private CookieOptions CookieOptionsFor(HttpContext context) => new()
    {
        Domain = CookieDomainFor(context.Request.Host.Host),
        Path = Address,
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
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
                throw new CryptographicException($"The sealed settings open to more than {MaxTextBytes} bytes.");
            }

            text.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length);
    }
}

/// <summary>
/// The address a page has the browser drop the settings an earlier DevKit kept in its cookies through. They are never
/// read: a cookie cannot show that DevKit sealed it, since anyone can seal settings that open as someone else's.
/// </summary>
internal static class ClientVaultEndpoints
{
    public static void MapClientVaultEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapDelete(ClientVault.Address, Clear);
    }

    /// <summary>Has this browser drop the settings an earlier DevKit kept in its cookies for the person signed in.</summary>
    internal static IResult Clear(HttpContext context, ClientVault vault, OwnerAccount owner)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(vault);
        if (SignedInUser.From(context.User, owner.Id) is not { } user)
        {
            return Results.Unauthorized();
        }

        context.Response.Headers.CacheControl = "no-store";
        vault.Clear(context, user);
        return Results.NoContent();
    }
}
