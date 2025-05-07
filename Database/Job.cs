using System.Text.Json.Serialization;

namespace GithubActionsOrchestrator.Database;

public class Job
{
    public int JobId { get; set; }
    public long GithubJobId { get; set; }
    public string Repository { get; set; }
    public string Owner { get; set; }
    public JobState State { get; set; }
    public DateTime InProgressTime { get; set; }
    public DateTime QueueTime { get; set; }
    public DateTime CompleteTime { get; set; }
    public string JobUrl { get; set; }
    
    //Relations
    public int? RunnerId { get; set; }

    [JsonIgnore]
    public Runner Runner { get; set; }
    public bool Orphan { get; set; }
    public string RequestedProfile { get; set; }
    public string RequestedSize { get; set; }
}