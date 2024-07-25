using System.Text.Json.Serialization;

namespace GithubActionsOrchestrator.GitHub;

public class GitHubRunner
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
    
    [JsonPropertyName("runner_group_id")]
    public int? RunnerGroupId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }
    
    [JsonPropertyName("os")]
    public string Os { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; }
    
    [JsonPropertyName("busy")]
    public bool Busy { get; set; }

    [JsonPropertyName("labels")]
    public List<GitHubRunnerLabel> Labels { get; set; }
}