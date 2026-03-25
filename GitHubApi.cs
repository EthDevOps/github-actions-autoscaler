using System.Net.Http.Headers;
using System.Text.Json;
using GithubActionsOrchestrator.GitHub;
using Npgsql.Replication;
using Serilog;
using Serilog.Core;

namespace GithubActionsOrchestrator;

public static class GitHubApi {
    private static readonly HttpClient SharedClient = CreateSharedClient();

    private static HttpClient CreateSharedClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hetzner-autoscale", "1"));
        return client;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string githubToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {githubToken}");
        return request;
    }

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
        var runners = new List<GitHubRunner>();
        string url = $"https://api.github.com/{ownerPath}/actions/runners?per_page=100";

        while (!string.IsNullOrEmpty(url))
        {
            using var request = CreateRequest(HttpMethod.Get, url, githubToken);
            HttpResponseMessage response = await SharedClient.SendAsync(request);
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
        using var request = CreateRequest(HttpMethod.Post, $"https://api.github.com/orgs/{orgName}/actions/runners/registration-token", githubToken);
        HttpResponseMessage response = await SharedClient.SendAsync(request);

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
        using var request = CreateRequest(HttpMethod.Post, $"https://api.github.com/repos/{repoName}/actions/runners/registration-token", githubToken);
        HttpResponseMessage response = await SharedClient.SendAsync(request);

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
        using var request = CreateRequest(HttpMethod.Delete, $"https://api.github.com/orgs/{orgName}/actions/runners/{runnerToRemove}", orgGitHubToken);
        HttpResponseMessage response = await SharedClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }
    public static async Task<bool> RemoveRunnerFromRepo(string repoName, string orgGitHubToken, long runnerToRemove)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"https://api.github.com/repos/{repoName}/actions/runners/{runnerToRemove}", orgGitHubToken);
        HttpResponseMessage response = await SharedClient.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public static async Task<(GitHubApiWorkflowRun Job, System.Net.HttpStatusCode StatusCode)> GetJobInfoForOrg(long stuckJobGithubJobId,string repoName, string orgGitHubToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"https://api.github.com/orgs/{repoName}/actions/jobs/{stuckJobGithubJobId}", orgGitHubToken);
        HttpResponseMessage response = await SharedClient.SendAsync(request);
        if(response.IsSuccessStatusCode)
        {
            string content = await response.Content.ReadAsStringAsync();
            GitHubApiWorkflowRun responseObject = JsonSerializer.Deserialize<GitHubApiWorkflowRun>(content);

            return (responseObject, response.StatusCode);
        }
        Log.Warning($"Unable to get GH job info for {repoName}/{stuckJobGithubJobId}: [{response.StatusCode}] {response.ReasonPhrase}");

        return (null, response.StatusCode);
    }
    public static async Task<(GitHubApiWorkflowRun Job, System.Net.HttpStatusCode StatusCode)> GetJobInfoForRepo(long stuckJobGithubJobId,string repoName, string orgGitHubToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"https://api.github.com/repos/{repoName}/actions/jobs/{stuckJobGithubJobId}", orgGitHubToken);
        HttpResponseMessage response = await SharedClient.SendAsync(request);
        if(response.IsSuccessStatusCode)
        {
            string content = await response.Content.ReadAsStringAsync();
            GitHubApiWorkflowRun responseObject = JsonSerializer.Deserialize<GitHubApiWorkflowRun>(content);

            return (responseObject, response.StatusCode);
        }
        Log.Warning($"Unable to get GH job info for {repoName}/{stuckJobGithubJobId}: [{response.StatusCode}] {response.ReasonPhrase}");

        return (null, response.StatusCode);
    }
}
