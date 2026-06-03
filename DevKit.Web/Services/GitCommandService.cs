using System.Diagnostics;
using System.Text.RegularExpressions;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

public class GitCommandService
{
    // Git ref names cannot contain whitespace or these characters, and a leading '-'
    // would be parsed as a command-line option rather than a branch operand.
    private static readonly char[] ForbiddenRefChars =
        { ' ', '\t', '~', '^', ':', '?', '*', '[', '\\', '"', '\'', ';', '|', '&', '$', '`', '\n', '\r' };

    private static readonly Regex ShaPattern = new("^[0-9a-fA-F]{7,40}$", RegexOptions.Compiled);

    private readonly ILogger<GitCommandService> _logger;

    public GitCommandService(ILogger<GitCommandService> logger)
    {
        _logger = logger;
    }

    private static bool IsValidRef(string? name)
    {
        var v = (name ?? "").Trim();
        return v.Length > 0 && !v.StartsWith('-') && v.IndexOfAny(ForbiddenRefChars) < 0;
    }

    private static bool IsValidSha(string? sha) => ShaPattern.IsMatch((sha ?? "").Trim());

    public async Task<CherryPickResult> CherryPickAsync(string repoPath, string targetBranch, string commitSha)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return new CherryPickResult { Status = CherryPickStatus.Error, ErrorOutput = $"Repository path not found: {repoPath}" };

        // Check if .git folder exists
        if (!Directory.Exists(Path.Combine(repoPath, ".git")))
            return new CherryPickResult { Status = CherryPickStatus.Error, ErrorOutput = $"Not a git repository: {repoPath}" };

        if (!IsValidRef(targetBranch))
            return new CherryPickResult { Status = CherryPickStatus.Error, ErrorOutput = $"Invalid target branch name: '{targetBranch}'" };

        if (!IsValidSha(commitSha))
            return new CherryPickResult { Status = CherryPickStatus.Error, ErrorOutput = $"Invalid commit SHA: '{commitSha}'" };

        targetBranch = targetBranch.Trim();
        commitSha = commitSha.Trim();

        try
        {
            // Remember current branch to restore later
            var currentBranch = (await RunGit(repoPath, "rev-parse", "--abbrev-ref", "HEAD")).Trim();

            // Step 1: fetch
            var fetchResult = await RunGit(repoPath, "fetch", "origin");
            _logger.LogInformation("git fetch: {Output}", fetchResult);

            // Step 2: checkout target branch
            var checkoutResult = await RunGit(repoPath, "checkout", targetBranch);
            if (checkoutResult.Contains("error"))
            {
                // Try creating tracking branch
                checkoutResult = await RunGit(repoPath, "checkout", "-b", targetBranch, $"origin/{targetBranch}");
            }

            // Step 3: pull latest
            await RunGit(repoPath, "pull", "origin", targetBranch);

            // Step 4: cherry-pick
            var cpResult = await RunGitFull(repoPath, "cherry-pick", commitSha);

            if (cpResult.ExitCode == 0)
            {
                // Step 5: push
                var pushResult = await RunGitFull(repoPath, "push", "origin", targetBranch);
                if (pushResult.ExitCode == 0)
                {
                    await RestoreBranch(repoPath, currentBranch, targetBranch);

                    return new CherryPickResult
                    {
                        Status = CherryPickStatus.Done,
                        Output = $"Cherry-pick successful.\n{cpResult.Output}\n{pushResult.Output}",
                        Branch = targetBranch
                    };
                }

                return new CherryPickResult
                {
                    Status = CherryPickStatus.Error,
                    Output = pushResult.Output,
                    ErrorOutput = $"Push failed: {pushResult.Error}",
                    Branch = targetBranch
                };
            }

            var errLower = (cpResult.Error + cpResult.Output).ToLower();

            // Check if commit is already applied
            if (errLower.Contains("empty") || errLower.Contains("nothing to commit") || errLower.Contains("already applied"))
            {
                await RunGit(repoPath, "cherry-pick", "--abort");
                await RestoreBranch(repoPath, currentBranch, targetBranch);

                return new CherryPickResult
                {
                    Status = CherryPickStatus.Exists,
                    Output = "Commit already exists in target branch.",
                    Branch = targetBranch
                };
            }

            // Conflict — leave the working tree in conflict state for manual resolution
            // Do NOT abort — user will resolve in Visual Studio and commit manually
            return new CherryPickResult
            {
                Status = CherryPickStatus.Conflict,
                Output = cpResult.Output,
                ErrorOutput = cpResult.Error,
                Branch = targetBranch
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cherry-pick failed for {Sha} on {Repo}", commitSha, repoPath);
            try { await RunGit(repoPath, "cherry-pick", "--abort"); }
            catch (Exception abortEx) { _logger.LogDebug(abortEx, "cherry-pick --abort during cleanup failed (likely nothing to abort)"); }
            return new CherryPickResult
            {
                Status = CherryPickStatus.Error,
                ErrorOutput = ex.Message,
                Branch = targetBranch
            };
        }
    }

    private async Task RestoreBranch(string repoPath, string currentBranch, string targetBranch)
    {
        if (IsValidRef(currentBranch) && currentBranch != targetBranch)
            await RunGit(repoPath, "checkout", currentBranch);
    }

    /// <summary>
    /// Runs an arbitrary read-only git command for callers that build a space-delimited
    /// argument string (currently the Merge Tool's remote-branch listing). The string is
    /// split into individual <see cref="ProcessStartInfo.ArgumentList"/> tokens, so each
    /// argument must not itself contain spaces.
    /// </summary>
    public async Task<string> RunGitCommand(string workingDir, string args)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return await RunGit(workingDir, tokens);
    }

    private async Task<string> RunGit(string workingDir, params string[] args)
    {
        var result = await RunGitFull(workingDir, args);
        return result.Output;
    }

    private async Task<(int ExitCode, string Output, string Error)> RunGitFull(string workingDir, params string[] args)
    {
        _logger.LogDebug("git {Args} in {Dir}", string.Join(' ', args), workingDir);
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync();
        var error = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        _logger.LogDebug("git exit={Code} out={Out} err={Err}", proc.ExitCode, output, error);
        return (proc.ExitCode, output, error);
    }
}
