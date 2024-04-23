namespace GithubActionsOrchestrator;

public class GitHubRunner
{
    public int id { get; set; }
    public int? runner_group_id { get; set; }
    public string name { get; set; }
    public string os { get; set; }
    public string status { get; set; }
    public bool busy { get; set; }
    public List<GitHubRunnerLabel> labels { get; set; }
}