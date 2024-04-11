namespace GithubActionsOrchestrator;

public class Machine
{
    public string Name { get; set; }
    public string Ipv4 { get; set; }
    public DateTime CreatedAt { get; set; }
    public long Id { get; set; }
    public long JobId { get; set; }
    public string? JobUrl { get; set; }
    public string? RepoName { get; set; }
    public DateTime JobPickedUpAt { get; set; }
    public string OrgName { get; set; }
    public string Size { get; set; }
    public string Arch { get; set; }
}