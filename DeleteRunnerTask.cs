namespace GithubActionsOrchestrator;

public record DeleteRunnerTask
{
    public int RetryCount { get; set; }
    public long ServerId { get; set; }
}