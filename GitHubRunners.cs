using System.Text.Json.Serialization;

namespace GithubActionsOrchestrator;

public class GitHubRunners
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("runners")]
    public List<GitHubRunner> Runners { get; set; }
}