using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The real domain lock, on this machine and as the account the tests run as: a key locked to that account opens again
/// for any lock of the same account, with nothing kept on disk, and a key locked to another account, or bytes that are
/// no locked key, never open. The tests that lock a key need a domain account, as shared DevKit does, and are skipped
/// elsewhere (<see cref="DomainAccountFactAttribute"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DomainKeyLockTests
{
    private const int KeyBytes = 32;

    private static readonly OwnerAccount ThisAccount = OwnerAccount.Current();

    [DomainAccountFact]
    public void A_key_locked_to_this_account_opens_again_for_any_lock_of_it()
    {
        var logger = new RecordingLogger<DomainKeyLock>();
        var keyLock = DomainKeyLock.For(ThisAccount, logger);
        var key = RandomNumberGenerator.GetBytes(KeyBytes);

        var locked = keyLock.Lock(key);

        Assert.Equal(key, keyLock.Unlock(locked));
        Assert.Equal(key, new DomainKeyLock(ThisAccount.Id).Unlock(locked));
        Assert.Empty(logger.Entries);
    }

    [DomainAccountFact]
    public void A_key_locked_to_another_account_does_not_open_here()
    {
        var krbtgt = new SecurityIdentifier(ThisAccount.Id).AccountDomainSid + "-502";
        var locked = new DomainKeyLock(krbtgt).Lock(RandomNumberGenerator.GetBytes(KeyBytes));

        Assert.Throws<IOException>(() => new DomainKeyLock(ThisAccount.Id).Unlock(locked));
    }

    [Fact]
    public void Bytes_that_are_no_locked_key_are_refused_as_such()
    {
        var notALockedKey = Enumerable.Repeat((byte)0x5A, 443).ToArray();

        Assert.Throws<CryptographicException>(() => new DomainKeyLock(ThisAccount.Id).Unlock(notALockedKey));
    }

    [Fact]
    public void The_account_shows_only_as_a_hash()
    {
        var keyLock = new DomainKeyLock(ThisAccount.Id);

        Assert.Matches("^[0-9a-f]{8}$", keyLock.Account);
        Assert.Equal(IKeyLock.AccountHash(ThisAccount.Id), keyLock.Account);
    }

    [Fact]
    public void A_lock_that_does_not_work_for_the_account_is_logged_at_start_and_DevKit_still_starts()
    {
        var logger = new RecordingLogger<DomainKeyLock>();

        DomainKeyLock.For(new OwnerAccount("S-1-5-21-1-2-3-4242"), logger);

        Assert.Equal(LogLevel.Error, Assert.Single(logger.Entries).Level);
    }
}

/// <summary>
/// A test that runs only as a domain account. The domain locks keys for nothing else, so as a local account, such as on
/// a CI runner outside the domain, the test is skipped and says why rather than failing.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DomainAccountFactAttribute : FactAttribute
{
    public DomainAccountFactAttribute()
    {
        // A local account's domain is the machine itself.
        if (string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Needs a domain account on a machine in the domain, which the domain locks keys for.";
        }
    }
}
