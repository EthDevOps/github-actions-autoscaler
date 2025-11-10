using GithubActionsOrchestrator.GitHub;

namespace GithubActionsOrchestrator.Models;

public class CreatedRunnersTracking
{
    public string Hostname { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public TargetType TargetType { get; set; }
    public string RepoName { get; set; } = string.Empty;
    public int RunnerDbId { get; set; }
    public bool IsStuckReplacement { get; set; }
    public int? StuckJobId { get; set; }
    public DateTime CreatedAt { get; set; }
}
