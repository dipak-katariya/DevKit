using DevKit.Web.Models;
using DevKit.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevKit.Tests;

/// <summary>
/// Which server and PAT the Settings form tries: the URL must be a valid http(s) address, a typed PAT always
/// wins, and a blank field means the stored PAT, but only for the server that PAT was saved for.
/// </summary>
[Collection(SettingsFileCollection.Name)]
public sealed class TfsConnectionFormTests : IDisposable
{
    private const string StoredUrl = "https://tfs.example.test/tfs/DefaultCollection";
    private const string StoredPat = "stored-test-pat-0a2b";
    private const string TypedPat = "typed-test-pat-4c5d";

    private readonly SettingsService _settings;

    public TfsConnectionFormTests()
    {
        File.Delete(TestSettingsLocation.SettingsPath);
        _settings = new SettingsService(NullLogger<SettingsService>.Instance);
        _settings.Tfs.Url = StoredUrl;
        _settings.Tfs.Pat = StoredPat;
    }

    public void Dispose() => File.Delete(TestSettingsLocation.SettingsPath);

    [Theory]
    [InlineData(null, "Server URL is required.")]
    [InlineData("   ", "Server URL is required.")]
    [InlineData("tfs.example.test/tfs", "Enter a valid http(s) URL, e.g. https://tfs.example.com/tfs/Collection.")]
    [InlineData("ftp://tfs.example.test/tfs", "Enter a valid http(s) URL, e.g. https://tfs.example.com/tfs/Collection.")]
    public void A_missing_or_invalid_url_is_refused_before_any_pat_is_looked_at(string? url, string error)
    {
        var candidate = TfsConnectionForm.Choose(url, TypedPat, _settings);

        Assert.False(candidate.IsValid);
        Assert.Equal(error, candidate.Error);
        Assert.Equal("", candidate.Pat);
    }

    [Fact]
    public void A_typed_pat_is_used_for_any_server_and_both_are_trimmed()
    {
        var candidate = TfsConnectionForm.Choose("  https://other.example.test/tfs/C  ", $"  {TypedPat}  ", _settings);

        Assert.True(candidate.IsValid);
        Assert.Equal("https://other.example.test/tfs/C", candidate.Url);
        Assert.Equal(TypedPat, candidate.Pat);
    }

    [Fact]
    public void A_blank_field_uses_the_stored_pat_for_its_own_server_however_it_is_typed()
    {
        var candidate = TfsConnectionForm.Choose(StoredUrl.ToUpperInvariant() + "/", "", _settings);

        Assert.True(candidate.IsValid);
        Assert.Equal(StoredPat, candidate.Pat);
    }

    [Fact]
    public void A_blank_field_for_another_server_asks_for_the_pat_again()
    {
        var candidate = TfsConnectionForm.Choose("https://other.example.test/tfs/C", null, _settings);

        Assert.False(candidate.IsValid);
        Assert.Equal("", candidate.Pat);
        Assert.Contains("belongs to a different server URL", candidate.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_field_with_nothing_stored_asks_for_a_pat()
    {
        _settings.Tfs.Pat = "";

        var candidate = TfsConnectionForm.Choose(StoredUrl, "  ", _settings);

        Assert.Equal("Personal Access Token is required.", candidate.Error);
    }

    [Theory]
    [InlineData("http://tfs.example.test/tfs", true)]
    [InlineData("https://tfs.example.test/tfs", false)]
    [InlineData("http://localhost:8080/tfs", false)]
    [InlineData("not a url", false)]
    public void Only_plain_http_to_another_machine_gets_the_unencrypted_warning(string url, bool warned)
    {
        Assert.Equal(warned, TfsConnectionForm.InsecureUrlWarning(url).Length > 0);
    }

    [Fact]
    public void A_pat_candidate_signs_in_with_the_token()
    {
        Assert.Equal(TfsAuthMode.Pat, TfsConnectionForm.Choose(StoredUrl, TypedPat, _settings).Auth);
        Assert.Equal(TfsAuthMode.Pat, TfsConnectionForm.Choose(StoredUrl, "", _settings).Auth);
    }

    [Fact]
    public void Windows_sign_in_needs_only_the_url_and_never_carries_a_pat_even_with_one_stored()
    {
        var candidate = TfsConnectionForm.ChooseWindowsSignIn("  https://other.example.test/tfs/C  ");

        Assert.True(candidate.IsValid);
        Assert.Equal(TfsAuthMode.Windows, candidate.Auth);
        Assert.Equal("https://other.example.test/tfs/C", candidate.Url);
        Assert.Equal("", candidate.Pat);
    }

    [Theory]
    [InlineData(null, "Server URL is required.")]
    [InlineData("   ", "Server URL is required.")]
    [InlineData("ftp://tfs.example.test/tfs", "Enter a valid http(s) URL, e.g. https://tfs.example.com/tfs/Collection.")]
    public void Windows_sign_in_refuses_a_missing_or_invalid_url(string? url, string error)
    {
        var candidate = TfsConnectionForm.ChooseWindowsSignIn(url);

        Assert.False(candidate.IsValid);
        Assert.Equal(error, candidate.Error);
        Assert.Equal("", candidate.Pat);
    }

    [Theory]
    [InlineData(TfsAuthMode.Pat, "your PAT")]
    [InlineData(TfsAuthMode.Windows, "your Windows sign-in")]
    public void The_unencrypted_warning_names_what_travels_in_the_clear(TfsAuthMode auth, string named)
    {
        Assert.Contains(named, TfsConnectionForm.InsecureUrlWarning("http://tfs.example.test/tfs", auth), StringComparison.Ordinal);
        Assert.Equal("", TfsConnectionForm.InsecureUrlWarning("https://tfs.example.test/tfs", auth));
    }
}
