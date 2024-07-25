using System.Text.Json.Serialization;

namespace GithubActionsOrchestrator.GitHub;

public class GitHubRunners
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("runners")]
    public List<GitHubRunner> Runners { get; set; }
}