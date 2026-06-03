using System.Diagnostics;
using DevKit.Web.Models;

namespace DevKit.Web.Services;

public class GitCommandService
{
    private readonly ILogger<GitCommandService> _logger;

    public GitCommandService(ILogger<GitCommandService> logger)
    {
        _logger = logger;
    }

    public async Task<CherryPickResult> CherryPickAsync(string repoPath, string targetBranch, string commitSha)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return new CherryPickResult { Status = CherryPickStatus.Error, ErrorOutput = $"Repository path not found: {repoPath}" };

        // Check if .git folder exists
        if (!Directory.Exists(Path.Combine(repoPath, ".git")))
            return new CherryPickResult { Status = CherryPickStatus.Error, ErrorOutput = $"Not a git repository: {repoPath}" };

        try
        {
            // Remember current branch to restore later
            var currentBranch = await RunGit(repoPath, "rev-parse --abbrev-ref HEAD");

            // Step 1: fetch
            var fetchResult = await RunGit(repoPath, "fetch origin");
            _logger.LogInformation("git fetch: {Output}", fetchResult);

            // Step 2: checkout target branch
            var checkoutResult = await RunGit(repoPath, $"checkout {targetBranch}");
            if (checkoutResult.Contains("error"))
            {
                // Try creating tracking branch
                checkoutResult = await RunGit(repoPath, $"checkout -b {targetBranch} origin/{targetBranch}");
            }

            // Step 3: pull latest
            await RunGit(repoPath, $"pull origin {targetBranch}");

            // Step 4: cherry-pick
            var cpResult = await RunGitFull(repoPath, $"cherry-pick {commitSha}");

            if (cpResult.ExitCode == 0)
            {
                // Step 5: push
                var pushResult = await RunGitFull(repoPath, $"push origin {targetBranch}");
                if (pushResult.ExitCode == 0)
                {
                    // Restore original branch
                    if (!string.IsNullOrWhiteSpace(currentBranch.Trim()) && currentBranch.Trim() != targetBranch)
                        await RunGit(repoPath, $"checkout {currentBranch.Trim()}");

                    return new CherryPickResult
                    {
                        Status = CherryPickStatus.Done,
                        Output = $"Cherry-pick successful.\n{cpResult.Output}\n{pushResult.Output}",
                        Branch = targetBranch
                    };
                }
                else
                {
                    return new CherryPickResult
                    {
                        Status = CherryPickStatus.Error,
                        Output = pushResult.Output,
                        ErrorOutput = $"Push failed: {pushResult.Error}",
                        Branch = targetBranch
                    };
                }
            }
            else
            {
                var errLower = (cpResult.Error + cpResult.Output).ToLower();

                // Check if commit is already applied
                if (errLower.Contains("empty") || errLower.Contains("nothing to commit") || errLower.Contains("already applied"))
                {
                    await RunGit(repoPath, "cherry-pick --abort");
                    if (!string.IsNullOrWhiteSpace(currentBranch.Trim()) && currentBranch.Trim() != targetBranch)
                        await RunGit(repoPath, $"checkout {currentBranch.Trim()}");

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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cherry-pick failed for {Sha} on {Repo}", commitSha, repoPath);
            // Try to clean up
            try { await RunGit(repoPath, "cherry-pick --abort"); } catch { }
            return new CherryPickResult
            {
                Status = CherryPickStatus.Error,
                ErrorOutput = ex.Message,
                Branch = targetBranch
            };
        }
    }

    public async Task<string> RunGitCommand(string workingDir, string args)
    {
        return await RunGit(workingDir, args);
    }

    private async Task<string> RunGit(string workingDir, string args)
    {
        var result = await RunGitFull(workingDir, args);
        return result.Output;
    }

    private async Task<(int ExitCode, string Output, string Error)> RunGitFull(string workingDir, string args)
    {
        _logger.LogDebug("git {Args} in {Dir}", args, workingDir);
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync();
        var error = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        _logger.LogDebug("git exit={Code} out={Out} err={Err}", proc.ExitCode, output, error);
        return (proc.ExitCode, output, error);
    }
}
