using GithubActionsOrchestrator.GitHub;

namespace GithubActionsOrchestrator.Models;

public class CreateTaskQueue
{
    public int CreateTaskQueueId { get; set; }
    public int RetryCount { get; set; }
    public TargetType TargetType { get; set; }
    public string RepoName { get; set; } = string.Empty;
    public int RunnerDbId { get; set; }
    public bool IsStuckReplacement { get; set; }
    public int? StuckJobId { get; set; }
    public DateTime QueuedAt { get; set; }
}
