using DevKit.Web.Models;

namespace DevKit.Web.Services;

/// <summary>
/// Everything settings know about repositories: the branches the merging sheet works with.
/// </summary>
public partial class SettingsService
{
    // ═══ CODE MERGING SHEET ═══

    /// <summary>Branch the merging sheet verifies against, remembered between sessions.</summary>
    public string CodeMergingTargetBranch
    {
        get => _settings.CodeMergingTargetBranch;
        set => Update(() => _settings.CodeMergingTargetBranch = value);
    }

    /// <summary>Repository ids last selected on the merging sheet.</summary>
    public List<string> CodeMergingRepoIds => _settings.CodeMergingRepoIds;

    public void SetCodeMergingRepoIds(IEnumerable<string> repoIds) =>
        Update(() => _settings.CodeMergingRepoIds = repoIds.Distinct().ToList());

    /// <summary>
    /// Superseded by <see cref="RepoBranches"/>, which is keyed by repository id. Kept so an
    /// older build still reads its own configuration, and as the migration source.
    /// </summary>
    public Dictionary<string, string> RepoTargetBranches => _settings.RepoTargetBranches;

    public void SetRepoTargetBranch(string repoName, string branch) => Update(() =>
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            _settings.RepoTargetBranches.Remove(repoName);
        }
        else
        {
            _settings.RepoTargetBranches[repoName] = branch.Trim();
        }
    });

    /// <summary>
    /// Per-repository branch defaults for the merging sheet, keyed by repository <em>id</em>.
    /// Names are not unique across projects (FOIAXpress-NextGen exists in two), so a
    /// name-keyed default silently applies to the wrong repository.
    /// </summary>
    public Dictionary<string, RepoBranchDefaults> RepoBranches => _settings.RepoBranches;

    public RepoBranchDefaults GetRepoBranches(string repoId) =>
        _settings.RepoBranches.TryGetValue(repoId, out var b) ? b : new RepoBranchDefaults();

    public void SetRepoQaBranch(string repoId, string branch) =>
        UpdateRepoBranches(repoId, b => b.QaBranch = (branch ?? "").Trim());

    public void SetRepoMergingBranch(string repoId, string branch) =>
        UpdateRepoBranches(repoId, b => b.MergingBranch = (branch ?? "").Trim());

    /// <summary>Drops an entry once both branches are blank, so cleared rows don't accumulate.</summary>
    private void UpdateRepoBranches(string repoId, Action<RepoBranchDefaults> apply)
    {
        if (string.IsNullOrWhiteSpace(repoId))
        {
            return;
        }

        Update(() =>
        {
            if (!_settings.RepoBranches.TryGetValue(repoId, out var entry))
            {
                entry = _settings.RepoBranches[repoId] = new RepoBranchDefaults();
            }

            apply(entry);
            if (entry.IsEmpty)
            {
                _settings.RepoBranches.Remove(repoId);
            }
        });
    }

    /// <summary>
    /// Folds the older name-keyed <see cref="RepoTargetBranches"/> and the single global
    /// <see cref="CodeMergingTargetBranch"/> into the id-keyed map, once the repository list
    /// is known. Existing entries always win, so this can run on every load without undoing
    /// anything configured since. Returns true when something was written.
    /// </summary>
    public bool MigrateRepoBranches(IReadOnlyList<TfsRepo> repos)
    {
        if (repos.Count == 0) return false;

        lock (_sync)
        {
            var changed = false;
            foreach (var repo in repos)
            {
                if (_settings.RepoBranches.ContainsKey(repo.Id))
                {
                    continue;
                }

                var qa = _settings.RepoTargetBranches.TryGetValue(repo.Name, out var legacy) ? legacy : "";

                // The old global verification branch only ever described the remembered repos, so
                // applying it to every repository would invent defaults nobody configured.
                var merging = _settings.CodeMergingRepoIds.Contains(repo.Id)
                    ? _settings.CodeMergingTargetBranch
                    : "";

                var entry = new RepoBranchDefaults { QaBranch = qa.Trim(), MergingBranch = merging.Trim() };
                if (entry.IsEmpty)
                {
                    continue;
                }

                _settings.RepoBranches[repo.Id] = entry;
                changed = true;
            }

            if (changed)
            {
                Save();
            }

            return changed;
        }
    }
}
