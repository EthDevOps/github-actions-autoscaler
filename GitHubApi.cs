using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using GithubActionsOrchestrator.GitHub;
using Npgsql.Replication;
using Serilog;
using Serilog.Core;

namespace GithubActionsOrchestrator;

public static class GitHubApi {
    public static async Task<List<GitHubRunner>> GetRunnersForOrg(string githubToken, string orgName)
    {
        return await GetRunners(githubToken, $"orgs/{orgName}");
    }
    
    public static async Task<List<GitHubRunner>> GetRunnersForRepo(string githubToken, string repoName)
    {
        return await GetRunners(githubToken, $"repos/{repoName}");
    }
    
    private static async Task<List<GitHubRunner>> GetRunners(string githubToken, string ownerPath)
    {
        
        // Register a runner with github
        using HttpClient client = new();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {githubToken}");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hetzner-autoscale", "1")); 
            

        var runners = new List<GitHubRunner>();
        string url = $"https://api.github.com/{ownerPath}/actions/runners?per_page=100";
        
        while (!string.IsNullOrEmpty(url))
        {
            HttpResponseMessage response = await client.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                string content = await response.Content.ReadAsStringAsync();
                var pageRunners = JsonSerializer.Deserialize<GitHubRunners>(content);
                if (pageRunners != null)
                {
                    runners.AddRange(pageRunners.Runners);
                }

                url = GetNextPageUrl(response);
            }
            else
            {
                Log.Warning($"Unable to get GH runners for org {ownerPath}: [{response.StatusCode}] {response.ReasonPhrase}");
                break;
            }
        }

        return runners;
    }
    private static string GetNextPageUrl(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Link", out var links))
        {
            foreach (var link in links)
            {
                // Example Link header format: <https://api.github.com/resource?page=2>; rel="next", ...
                foreach (var part in link.Split(','))
                {
                    if (part.Contains("rel=\"next\""))
                    {
                        int startIndex = part.IndexOf('<') + 1;
                        int endIndex = part.IndexOf('>');
                        if (startIndex >= 0 && endIndex > startIndex)
                        {
                            return part[startIndex..endIndex];
                        }
                    }
                }
            }
        }

        return null;
    }

    
    public static async Task<string> GetRunnerTokenForOrg(string githubToken, string orgName)
    {
        
        // Register a runner with github
        using HttpClient client = new();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {githubToken}");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hetzner-autoscale", "1")); 
            
        HttpResponseMessage response = await client.PostAsync(
            $"https://api.github.com/orgs/{orgName}/actions/runners/registration-token", null);

        if(response.IsSuccessStatusCode)
        {
            string content = await response.Content.ReadAsStringAsync();
            GitHubResponse responseObject = JsonSerializer.Deserialize<GitHubResponse>(content);
            return responseObject?.token;
        }
        Log.Warning($"Unable to get GH runner token for org {orgName}: [{response.StatusCode}] {response.ReasonPhrase}");

        return null;
    }
    public static async Task<string> GetRunnerTokenForRepo(string githubToken, string repoName)
    {
        
        // Register a runner with github
        using HttpClient client = new();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {githubToken}");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hetzner-autoscale", "1")); 
            
        HttpResponseMessage response = await client.PostAsync(
            $"https://api.github.com/repos/{repoName}/actions/runners/registration-token", null);

        if(response.IsSuccessStatusCode)
        {
            string content = await response.Content.ReadAsStringAsync();
            GitHubResponse responseObject = JsonSerializer.Deserialize<GitHubResponse>(content);
            return responseObject?.token;
        }
        Log.Warning($"Unable to get GH runner token for repo {repoName}: [{response.StatusCode}] {response.ReasonPhrase}");

        return null;
    }

    public static async Task<bool> RemoveRunnerFromOrg(string orgName, string orgGitHubToken, long runnerToRemove)
    {
        // Register a runner with github
        using HttpClient client = new();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {orgGitHubToken}");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hetzner-autoscale", "1"));

        HttpResponseMessage response = await client.DeleteAsync(
            $"https://api.github.com/orgs/{orgName}/actions/runners/{runnerToRemove}");
        return response.IsSuccessStatusCode;

    }
    public static async Task<bool> RemoveRunnerFromRepo(string repoName, string orgGitHubToken, long runnerToRemove)
    {
        // Register a runner with github
        using HttpClient client = new();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {orgGitHubToken}");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hetzner-autoscale", "1"));

        HttpResponseMessage response = await client.DeleteAsync(
            $"https://api.github.com/repos/{repoName}/actions/runners/{runnerToRemove}");
        return response.IsSuccessStatusCode;

    }

    public static async Task<GitHubApiWorkflowRun> GetJobInfoForOrg(long stuckJobGithubJobId,string repoName, string orgGitHubToken)
    {
        using HttpClient client = new();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {orgGitHubToken}");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hetzner-autoscale", "1"));

        HttpResponseMessage response = await client.GetAsync(
            $"https://api.github.com/orgs/{repoName}/actions/jobs/{stuckJobGithubJobId}");
        if(response.IsSuccessStatusCode)
        {
            string content = await response.Content.ReadAsStringAsync();
            GitHubApiWorkflowRun responseObject = JsonSerializer.Deserialize<GitHubApiWorkflowRun>(content);
            
            return responseObject;
        }
        Log.Warning($"Unable to get GH job info for {repoName}/{stuckJobGithubJobId}: [{response.StatusCode}] {response.ReasonPhrase}");

        return null;
    }
    public static async Task<GitHubApiWorkflowRun> GetJobInfoForRepo(long stuckJobGithubJobId,string repoName, string orgGitHubToken)
    {
        using HttpClient client = new();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {orgGitHubToken}");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hetzner-autoscale", "1"));

        HttpResponseMessage response = await client.GetAsync(
            $"https://api.github.com/repos/{repoName}/actions/jobs/{stuckJobGithubJobId}");
        if(response.IsSuccessStatusCode)
        {
            string content = await response.Content.ReadAsStringAsync();
            GitHubApiWorkflowRun responseObject = JsonSerializer.Deserialize<GitHubApiWorkflowRun>(content);
            
            return responseObject;
        }
        Log.Warning($"Unable to get GH job info for {repoName}/{stuckJobGithubJobId}: [{response.StatusCode}] {response.ReasonPhrase}");

        return null;
    }
}

public class GitHubApiWorkflowJob
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("conclusion")]
    public string Conclusion { get; set; }

    [JsonPropertyName("number")]
    public int? Number { get; set; }

    [JsonPropertyName("started_at")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; set; }
}

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
