using System.Runtime.Versioning;
using System.Security.Principal;
using DevKit.Web.Services;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// How much password guessing shared DevKit lets through, and who may sign in at all when devkit.json names groups.
/// </summary>
public sealed class SignInLimitsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly SignedInUser Alice = new("S-1-5-21-1-1001", @"MT\alice", IsOwner: false);

    [Fact]
    public async Task Wrong_passwords_sent_in_parallel_reach_Windows_no_more_often_than_one_after_another()
    {
        using var checker = new SlowCredentialChecker();
        var windows = new WindowsSignIn(checker, new SignInThrottle(new ManualClock()), new OwnerAccount("S-1-5-21-9"), ["localhost"]);

        // A thread each: the checks Windows holds keep theirs, and the other tests' work must not hold up the refusals.
        var attempts = Enumerable.Range(0, 10)
            .Select(_ => Task.Factory.StartNew(() => windows.CheckPassword("alice", "guess"), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToList();
        Assert.True(await PortHandover.WaitUntilAsync(() => checker.Calls == SignInThrottle.MaxFailures, Patience, CancellationToken.None));
        Assert.True(await PortHandover.WaitUntilAsync(() => attempts.Count(attempt => attempt.IsCompleted) == 10 - SignInThrottle.MaxFailures, Patience, CancellationToken.None));
        checker.Answer();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(SignInThrottle.MaxFailures, checker.Calls);
        Assert.Equal(10 - SignInThrottle.MaxFailures, results.Count(result => result.Detail == "stopped after too many wrong passwords"));
        Assert.All(results, result => Assert.Null(result.User));
    }

    [Fact]
    public void Many_accounts_guessed_at_once_make_every_typed_password_wait_a_minute()
    {
        var clock = new ManualClock();
        var checker = new FakeCredentialChecker();
        checker.Add(@"MT\alice", "right", Alice);
        var windows = new WindowsSignIn(checker, new SignInThrottle(clock), new OwnerAccount("S-1-5-21-9"), ["localhost"]);
        for (var account = 0; account < SignInThrottle.MaxFailuresForEveryone; account++)
        {
            Assert.Null(windows.CheckPassword($"guessed{account}", "Summer2026").User);
        }

        var stopped = windows.CheckPassword("alice", "right");

        Assert.Null(stopped.User);
        Assert.Contains("on this DevKit", stopped.Error, StringComparison.Ordinal);
        Assert.Equal(SignInThrottle.MaxFailuresForEveryone, checker.Calls);

        clock.Now += SignInThrottle.EveryoneWindow;
        Assert.Equal(Alice, windows.CheckPassword("alice", "right").User);
    }

    [Fact]
    public void A_right_password_ends_its_attempt_so_the_next_ones_are_not_held_back()
    {
        var checker = new FakeCredentialChecker();
        checker.Add(@"MT\alice", "right", Alice);
        var windows = new WindowsSignIn(checker, new SignInThrottle(new ManualClock()), new OwnerAccount("S-1-5-21-9"), ["localhost"]);

        for (var visit = 0; visit < 10; visit++)
        {
            Assert.Equal(Alice, windows.CheckPassword("alice", "right").User);
        }
    }

    /// <summary>Windows' password check that answers "wrong" only once the test says so, counting who asked.</summary>
    private sealed class SlowCredentialChecker : IWindowsCredentialChecker, IDisposable
    {
        private readonly ManualResetEventSlim _answer = new();
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Answer() => _answer.Set();

        public CredentialCheck Check(string userName, string password)
        {
            Interlocked.Increment(ref _calls);
            _answer.Wait(Patience);
            return CredentialCheck.Refused("Windows did not accept this username and password.", "wrong username or password");
        }

        public string AccountKey(string userName) => @"MT\" + userName.Trim().ToUpperInvariant();

        public void Dispose() => _answer.Dispose();
    }
}

/// <summary>devkit.json's AllowedGroups: the groups whose members may sign in, looked up once at start.</summary>
[SupportedOSPlatform("windows")]
public sealed class AllowedGroupsTests
{
    private static readonly string Everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value;
    private static readonly string NoSuchGroup = "S-1-5-21-1-2-3-987654";

    [Fact]
    public void Without_groups_everyone_the_domain_knows_may_sign_in()
    {
        using var me = WindowsIdentity.GetCurrent();

        Assert.True(AllowedGroups.Admit(me, new HashSet<string>(), "S-1-5-21-9"));
    }

    [Fact]
    public void A_member_of_a_named_group_may_sign_in_and_anyone_else_may_not()
    {
        using var me = WindowsIdentity.GetCurrent();

        Assert.True(AllowedGroups.Admit(me, new HashSet<string> { Everyone }, "S-1-5-21-9"));
        Assert.False(AllowedGroups.Admit(me, new HashSet<string> { NoSuchGroup }, "S-1-5-21-9"));
    }

    [Fact]
    public void The_account_DevKit_runs_as_may_always_sign_in()
    {
        using var me = WindowsIdentity.GetCurrent();

        Assert.True(AllowedGroups.Admit(me, new HashSet<string> { NoSuchGroup }, me.User!.Value));
    }

    [Fact]
    public void An_account_named_in_the_list_itself_may_sign_in()
    {
        using var me = WindowsIdentity.GetCurrent();

        Assert.True(AllowedGroups.Admit(me, new HashSet<string> { me.User!.Value }, "S-1-5-21-9"));
    }

    // The password path reads the groups from the token Windows hands back, as WindowsIdentity reads them.
    [Fact]
    public void A_tokens_account_and_groups_are_read_as_Windows_counts_them()
    {
        using var me = WindowsIdentity.GetCurrent();

        Assert.Equal(me.User, WindowsTokens.User(me.AccessToken));
        Assert.Equal(me.Groups!.Select(group => group.Value).ToHashSet(), WindowsTokens.Groups(me.AccessToken).Select(group => group.Value).ToHashSet());
        Assert.True(AllowedGroups.Admit(WindowsTokens.User(me.AccessToken), WindowsTokens.Groups(me.AccessToken), new HashSet<string> { Everyone }, "S-1-5-21-9"));
    }

    [Fact]
    public void Groups_are_read_by_name_or_as_SIDs_once_at_start()
    {
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var usersName = ((NTAccount)users.Translate(typeof(NTAccount))).Value;

        var ids = DevKitHostingOptions.GroupIds([usersName, Everyone, "  ", NoSuchGroup.ToLowerInvariant()]);

        Assert.Equal(new HashSet<string>([users.Value, Everyone, NoSuchGroup], StringComparer.OrdinalIgnoreCase), ids);
    }

    [Theory]
    [InlineData(@"MT\devkit-no-such-group-7f3a", "which Windows does not know")]
    [InlineData("S-1-not-a-sid", "which is not a SID")]
    public void A_group_Windows_does_not_know_stops_DevKit_from_starting(string group, string reason)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => DevKitHostingOptions.GroupIds([group]));

        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }
}
