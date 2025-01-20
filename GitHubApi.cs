using System.Net.Http.Headers;
using System.Text.Json;
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

    public static async Task<GitHubJob> GetJobInfo(long stuckJobGithubJobId,string repoName, string orgGitHubToken)
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
            GitHubJob responseObject = JsonSerializer.Deserialize<GitHubJob>(content);
            
            return responseObject;
        }
        Log.Warning($"Unable to get GH job info for {repoName}/{stuckJobGithubJobId}: [{response.StatusCode}] {response.ReasonPhrase}");

        return null;
    }
}

public class GitHubJob
{

    public int Id { get; set; }
    public int RunId { get; set; }
    public string RunUrl { get; set; }
    public int RunAttempt { get; set; }
    public string NodeId { get; set; }
    public string HeadSha { get; set; }
    public string Url { get; set; }
    public string HtmlUrl { get; set; }
    public string Status { get; set; }
    public string Conclusion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; } // Nullable
    public string Name { get; set; }
    public List<GitHubJobStep> Steps { get; set; }
    public string CheckRunUrl { get; set; }
    public List<string> Labels { get; set; }
    public int? RunnerId { get; set; } // Nullable
    public string RunnerName { get; set; } // Nullable
    public int? RunnerGroupId { get; set; } // Nullable
    public string RunnerGroupName { get; set; } // Nullable
    public string WorkflowName { get; set; } // Nullable
    public string HeadBranch { get; set; } // Nullable

}

public class GitHubJobStep
    {
        public string Status { get; set; }
        public string Conclusion { get; set; }
        public string Name { get; set; }
        public int Number { get; set; }
        public DateTime? StartedAt { get; set; } // Nullable
        public DateTime? CompletedAt { get; set; } // Nullable
    }

