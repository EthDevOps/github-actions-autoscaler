using System.Net.Http.Headers;
using System.Text.Json;

namespace GithubActionsOrchestrator;

public class GitHubRunnerLabel
{
    public int id { get; set; }
    public string name { get; set; }
    public string type { get; set; }
}

public class GitHubRunner
{
    public int id { get; set; }
    public int? runner_group_id { get; set; }
    public string name { get; set; }
    public string os { get; set; }
    public string status { get; set; }
    public bool busy { get; set; }
    public List<GitHubRunnerLabel> labels { get; set; }
}

public class GitHubRunners
{
    public int total_count { get; set; }
    public List<GitHubRunner> runners { get; set; }
}


public class GitHubApi {
    public static async Task<GitHubRunners> GetRunners(string githubToken, string orgName)
    {
        
        // Register a runner with github
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {githubToken}");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hetzner-autoscale", "1")); 
            
        var response = await client.GetAsync(
            $"https://api.github.com/orgs/{orgName}/actions/runners");

        if(response.IsSuccessStatusCode)
        {
            string content = await response.Content.ReadAsStringAsync();
            var responseObject = JsonSerializer.Deserialize<GitHubRunners>(content);
            if (responseObject == null)
            {
                return null;
            }
            return responseObject;
        }
        else
        {
            return null;
        }
    }
    public static async Task<string> GetRunnerToken(string githubToken, string orgName)
    {
        
        // Register a runner with github
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {githubToken}");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hetzner-autoscale", "1")); 
            
        var response = await client.PostAsync(
            $"https://api.github.com/orgs/{orgName}/actions/runners/registration-token", null);

        if(response.IsSuccessStatusCode)
        {
            string content = await response.Content.ReadAsStringAsync();
            var responseObject = JsonSerializer.Deserialize<GitHubResponse>(content);
            if (responseObject == null)
            {
                return null;
            }
            return responseObject.token;
        }
        else
        {
            return null;
        }
    }

    public static async Task<bool> RemoveRunner(string orgName, string orgGitHubToken, long runnerToRemove)
    {
        // Register a runner with github
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {orgGitHubToken}");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hetzner-autoscale", "1"));

        var response = await client.DeleteAsync(
            $"https://api.github.com/orgs/{orgName}/actions/runners/{runnerToRemove}");
        return response.IsSuccessStatusCode;

    }
}