using GithubActionsOrchestrator.GitHub;

namespace GithubActionsOrchestrator.Models;

public record CreateRunnerTask
{
    public string Arch { get; set; }
    public string Size { get; set; }
    public string RunnerToken { get; set; }
    public string OrgName { get; set; }
    public int RetryCount { get; set; }
    public long ServerId { get; set; }
    public bool IsCustom { get; set; }
    public string ProfileName { get; set; }
    public TargetType TargetType { get; set; }
    public string RepoName { get; set; }
}