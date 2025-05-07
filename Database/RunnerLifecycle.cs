namespace GithubActionsOrchestrator.Database;

public class RunnerLifecycle
{
    public int RunnerLifecycleId { get; set; }
    public DateTime EventTimeUtc { get; set; }
    public string Event { get; set; }
    public RunnerStatus Status { get; set; }
}