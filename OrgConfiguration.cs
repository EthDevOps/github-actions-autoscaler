namespace GithubActionsOrchestrator;

public class OrgConfiguration
{
    public string OrgName { get; set; }
    public string GitHubToken { get; set; }
    public List<Pool> Pools { get; set; }
}