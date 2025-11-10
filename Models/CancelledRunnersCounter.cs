namespace GithubActionsOrchestrator.Models;

public class CancelledRunnersCounter
{
    public int CancelledRunnersCounterId { get; set; }
    public string Owner { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string Profile { get; set; } = string.Empty;
    public string Arch { get; set; } = string.Empty;
    public int Count { get; set; }
}
