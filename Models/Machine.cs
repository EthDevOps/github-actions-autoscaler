namespace GithubActionsOrchestrator.Models;

public class Machine
{
    public string Name { get; set; }
    public string Ipv4 { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.MinValue;
    public long Id { get; set; }
    public long JobId { get; set; }
    public string JobUrl { get; set; }
    public string RepoName { get; set; }
    public DateTime JobPickedUpAt { get; set; } = DateTime.MinValue;
    public string TargetName { get; set; }
    public string Size { get; set; }
    public string Arch { get; set; }
    public string Profile { get; set; }
    public bool IsCustom { get; set; }
}