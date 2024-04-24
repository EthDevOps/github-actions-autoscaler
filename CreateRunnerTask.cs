namespace GithubActionsOrchestrator;

public record CreateRunnerTask
{
    public string Arch { get; set; }
    public string Size { get; set; }
    public string RunnerToken { get; set; }
    public string OrgName { get; set; }
    public int RetryCount { get; set; }
    public long ServerId { get; set; }
    public string ScriptName { get; set; }
    public int ScriptVersion { get; set; }
    public bool IsCustom { get; set; }
}