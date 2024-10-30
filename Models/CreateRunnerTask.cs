using GithubActionsOrchestrator.GitHub;

namespace GithubActionsOrchestrator.Models;

public record CreateRunnerTask
{
    public string RunnerToken { get; set; }
    public int RetryCount { get; set; }
    public TargetType TargetType { get; set; }
    public string RepoName { get; set; }
    public int RunnerDbId { get; set; }
    public bool IsStuckReplacement { get; set; }
    public int? StuckJobId { get; set; }
}