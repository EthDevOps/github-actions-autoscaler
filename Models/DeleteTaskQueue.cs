namespace GithubActionsOrchestrator.Models;

public class DeleteTaskQueue
{
    public int DeleteTaskQueueId { get; set; }
    public int RetryCount { get; set; }
    public long ServerId { get; set; }
    public int RunnerDbId { get; set; }
    public DateTime QueuedAt { get; set; }
}
