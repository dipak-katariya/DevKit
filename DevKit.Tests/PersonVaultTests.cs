using System.Buffers.Text;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// The server's settings folder: open to the account DevKit runs as alone, refused the moment it is opened up or
/// replaced by a link, one whole file per person written one save at a time, and the same folder whatever package or
/// port DevKit runs from. Settings earlier DevKits kept in the browser's cookies move in once.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PersonVaultTests : SharedSettingsScratch
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;

    /// <summary>Lists Everyone on <paramref name="folder"/>, as an administrator opening it up would.</summary>
    internal static void OpenToEveryone(string folder)
    {
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [Fact]
    public void The_folder_is_open_to_the_account_DevKit_runs_as_alone()
    {
        var security = new DirectoryInfo(ServerPath).GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(Me, security.GetOwner(typeof(SecurityIdentifier)));
        var rule = Assert.Single(rules);
        Assert.Equal(Me, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
    }

    [Fact]
    public void A_file_takes_only_what_the_folder_gives()
    {
        Keep(Alice, SealedNumber(1));
        var file = new FileInfo(Server.PathFor(Alice));

        var rules = file.GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();

        Assert.All(rules, rule => Assert.Equal(Me, rule.IdentityReference));
        Assert.Empty(file.GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)));
    }

    [Fact]
    public void A_folder_opened_to_others_is_refused_until_DevKit_closes_it_again()
    {
        Keep(Alice, SealedNumber(1));
        OpenToEveryone(ServerPath);

        Assert.Throws<UnauthorizedAccessException>(() => Server.Load(Alice));
        Assert.Throws<UnauthorizedAccessException>(() => Server.Update(Alice, _ => SealedNumber(2)));

        Server.Secure();

        Assert.Equal(SealedNumber(1), Server.Load(Alice));
    }

    [Fact]
    public void A_file_given_permissions_of_its_own_is_closed_again()
    {
        Keep(Alice, SealedNumber(1));
        var file = new FileInfo(Server.PathFor(Alice));
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(security);

        Assert.Throws<UnauthorizedAccessException>(() => Server.Load(Alice));

        NewServer().Secure();

        Assert.Empty(file.GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)));
        Assert.Equal(SealedNumber(1), Server.Load(Alice));
    }

    [Fact]
    public void Every_file_kept_is_owned_by_the_account_DevKit_runs_as()
    {
        Keep(Alice, SealedNumber(1));
        Keep(Alice, SealedNumber(2));

        Assert.All(Directory.GetFiles(ServerPath, "*.sealed*"),
            path => Assert.Equal(Me, new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier))));
    }

    [Fact]
    public void A_link_in_place_of_the_folder_is_never_used()
    {
        var elsewhere = Path.Combine(Root, "elsewhere");
        var linked = Path.Combine(Root, "linked-vault");
        MakeJunction(linked, elsewhere);
        var vault = new PersonVault(linked, Me, NullLogger<PersonVault>.Instance);

        Assert.Throws<UnauthorizedAccessException>(vault.Secure);
        Assert.Throws<UnauthorizedAccessException>(() => vault.Load(Alice));
        Assert.Empty(Directory.GetFiles(elsewhere));
    }

    [Fact]
    public void Each_file_lies_in_the_folder_and_names_nobody()
    {
        var path = Server.PathFor(Alice);

        Assert.Equal(ServerPath, Path.GetDirectoryName(path));
        Assert.DoesNotContain(Alice.Id, path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", path, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(path, Server.PathFor(Bob));
    }

    [Fact]
    public void Each_save_replaces_the_file_whole_and_keeps_the_one_before_as_backup()
    {
        Keep(Alice, SealedNumber(1));
        Keep(Alice, SealedNumber(2));

        Assert.Equal(SealedNumber(2), Server.Load(Alice));
        Assert.Equal(SealedNumber(1), Server.LoadBackup(Alice));
        Assert.Equal(2, Directory.GetFiles(ServerPath, "*.sealed*").Length);

        Server.DeleteBackup(Alice);
        Assert.Null(Server.LoadBackup(Alice));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"TeamName\":\"plain text\"}")]
    [InlineData("v2.not.sealed")]
    public void Only_sealed_settings_are_kept(string text)
    {
        Assert.Throws<IOException>(() => Server.Update(Alice, _ => text));
        Assert.Null(Server.Load(Alice));
    }

    [Fact]
    public async Task A_file_larger_than_settings_ever_are_is_not_read()
    {
        Keep(Alice, SealedNumber(1));
        await File.WriteAllTextAsync(Server.PathFor(Alice), new string('A', ClientVault.MaxSealedChars + 1));

        Assert.Throws<IOException>(() => Server.Load(Alice));
    }

    [Fact]
    public void Saves_at_once_from_two_DevKit_processes_each_build_on_the_other()
    {
        var other = NewServer();
        Keep(Alice, SealedNumber(0));

        Parallel.For(0, 60, turn =>
            (turn % 2 == 0 ? Server : other).Update(Alice, kept => SealedNumber(NumberIn(kept!) + 1)));

        Assert.Equal(60, NumberIn(Server.Load(Alice)!));
        Assert.Empty(Directory.GetFiles(ServerPath, "*.partial"));
    }

    [Fact]
    public async Task Settings_survive_a_redeploy_a_package_switch_another_port_and_a_browser_that_deleted_everything()
    {
        var alice = await SignedInAsync(Alice);
        alice.SaveTfs(FixedUrl, AlicePat);
        alice.TeamName = "TEAM-A";

        // A new DevKit process (another package, another port) opens the same folder; the browser kept nothing.
        var afterRedeploy = await SignedInAsync(Alice, new FakeBrowserStorage(), server: NewServer());

        Assert.Equal("TEAM-A", afterRedeploy.TeamName);
        Assert.Equal(AlicePat, afterRedeploy.Tfs.Pat);
        Assert.False(afterRedeploy.StartedEmpty);
        Assert.Null(afterRedeploy.LoadProblem);
    }

    /// <summary>
    /// Anyone can seal settings that open as someone else's and plant them in a browser from any site of the domain, so
    /// a sealed copy in the cookies, here a forged PAT, is never taken; the browser is only told to drop it.
    /// </summary>
    [Fact]
    public async Task Settings_sealed_in_the_browser_cookies_are_never_taken_and_the_browser_drops_them()
    {
        Browser.Vault = Vault.Seal(Alice, Vault.NewKey(), $$$"""{"TeamName":"PLANTED","Tfs":{"Url":"{{{FixedUrl}}}","Pat":"forged-pat"}}""");

        var alice = await SignedInAsync(Alice);

        Assert.Equal("", alice.TeamName);
        Assert.Equal("", alice.Tfs.Pat);
        Assert.Null(KeptFor(Alice));
        Assert.Null(Browser.Vault);
        Assert.Equal(1, Browser.VaultClears);
        Assert.Null(alice.LoadProblem);
    }

    [Fact]
    public async Task A_sealed_cookie_copy_that_does_not_open_never_stops_a_save()
    {
        Browser.Vault = Vault.Seal(Alice, Vault.NewKey(), """{"TeamName":"WAITING"}""");
        KeyLock.Unavailable = true;

        var alice = await SignedInAsync(Alice);
        KeyLock.Unavailable = false;
        alice.TeamName = "SAVED";

        Assert.Null(alice.LastSaveError);
        Assert.Equal("SAVED", (await SignedInAsync(Alice)).TeamName);
    }

    /// <summary>Text with the shape of sealed settings, carrying <paramref name="number"/>, for the folder's own tests.</summary>
    private static string SealedNumber(int number)
    {
        var payload = new byte[32];
        BitConverter.TryWriteBytes(payload, number);
        return $"v2.c2668da0.bG9ja2Vk.{Base64Url.EncodeToString(payload)}";
    }

    private static int NumberIn(string sealedText) => BitConverter.ToInt32(Base64Url.DecodeFromChars(sealedText.Split('.')[3]));
}
