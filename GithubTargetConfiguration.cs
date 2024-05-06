namespace GithubActionsOrchestrator;

public class GithubTargetConfiguration
{
    public string Name { get; set; }
    public string GitHubToken { get; set; }
    public List<Pool> Pools { get; set; }
    
    public TargetType Target { get; set; }
}

public enum TargetType
{
    Repository = 0,
    Organization = 1
}
