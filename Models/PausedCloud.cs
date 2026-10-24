namespace GithubActionsOrchestrator.Models;

public class PausedCloud
{
    public string Cloud { get; set; }
    public DateTime PausedAt { get; set; }
    public string? PausedBy { get; set; }
}
