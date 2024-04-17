namespace GithubActionsOrchestrator;

public record RunnerTask
{
    public string Arch { get; set; }
    public string Size { get; set; }
    public string RunnerToken { get; set; }
    public string OrgName { get; set; }
    public int RetryCount { get; set; }
    public RunnerAction Action { get; set; }
    public long ServerId { get; set; }
}