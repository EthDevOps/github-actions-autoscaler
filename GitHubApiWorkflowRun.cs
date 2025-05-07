using System.Text.Json.Serialization;

namespace GithubActionsOrchestrator;

public class GitHubApiWorkflowRun
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("run_id")]
    public long? RunId { get; set; }

    [JsonPropertyName("workflow_name")]
    public string WorkflowName { get; set; }

    [JsonPropertyName("head_branch")]
    public string HeadBranch { get; set; }

    [JsonPropertyName("run_url")]
    public string RunUrl { get; set; }

    [JsonPropertyName("run_attempt")]
    public int? RunAttempt { get; set; }

    [JsonPropertyName("node_id")]
    public string NodeId { get; set; }

    [JsonPropertyName("head_sha")]
    public string HeadSha { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("conclusion")]
    public string Conclusion { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }

    [JsonPropertyName("started_at")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("steps")]
    public List<GitHubApiWorkflowJob> Steps { get; set; }

    [JsonPropertyName("check_run_url")]
    public string CheckRunUrl { get; set; }

    [JsonPropertyName("labels")]
    public List<string> Labels { get; set; }

    [JsonPropertyName("runner_id")]
    public long? RunnerId { get; set; }

    [JsonPropertyName("runner_name")]
    public string RunnerName { get; set; }

    [JsonPropertyName("runner_group_id")]
    public int? RunnerGroupId { get; set; }

    [JsonPropertyName("runner_group_name")]
    public string RunnerGroupName { get; set; }
}