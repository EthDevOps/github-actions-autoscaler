namespace GithubActionsOrchestrator.Database;

public class Runner
{
    public string IPv4 { get; set; }
    public int RunnerId { get; set; }
    public string Cloud { get; set; }
    public string Hostname { get; set; }
    public string Size { get; set; }
    public string Profile { get; set; }
    
    // Relations

    public int? JobId { get; set; }
    public Job Job { get; set; }
    public ICollection<RunnerLifecycle> Lifecycle { get; set; }
    public long CloudServerId { get; set; }

    public DateTime CreateTime
    {
        get
        {
            return Lifecycle.FirstOrDefault(x => x.Status == RunnerStatus.CreationQueued)!.EventTimeUtc;
        }
    }

    public RunnerStatus LastState
    {
        get
        {
            return Lifecycle.MaxBy(x => x.EventTimeUtc).Status;
        }
    }

    public bool IsOnline { get; set; }
    public string Arch { get; set; }
    public string Owner { get; set; }
    public bool IsCustom { get; set; }

    public DateTime LastStateTime
    {
        get
        {
            return Lifecycle.MaxBy(x => x.EventTimeUtc).EventTimeUtc;
        }
    }

    public bool StuckJobReplacement { get; set; } = false;
    public bool UsePrivateNetwork { get; set; }
    public string ProvisionId { get; set; }
    public string ProvisionPayload { get; set; }
}