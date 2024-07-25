using System.Text.Json.Serialization;

namespace GithubActionsOrchestrator.GitHub;

public class GitHubRunnerLabel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }
}