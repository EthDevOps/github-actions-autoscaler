using System.Net.Http.Headers;
using System.Text.Json;
using GithubActionsOrchestrator.GitHub;
using Npgsql.Replication;
using Serilog;
using Serilog.Core;

namespace GithubActionsOrchestrator;

public static class GitHubApi {
    public static async Task<GitHubRunners> GetRunnersForOrg(string githubToken, string orgName)
    {
        
        // Register a runner with github
        using HttpClient client = new();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {githubToken}");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hetzner-autoscale", "1")); 
            
        HttpResponseMessage response = await client.GetAsync(
            $"https://api.github.com/orgs/{orgName}/actions/runners");

        if(response.IsSuccessStatusCode)
        {
            string content = await response.Content.ReadAsStringAsync();
            GitHubRunners responseObject = JsonSerializer.Deserialize<GitHubRunners>(content);
            return responseObject;
        }
        
        Log.Warning($"Unable to get GH runners for org: [{response.StatusCode}] {response.ReasonPhrase}");

        return null;
    }
    public static async Task<GitHubRunners> GetRunnersForRepo(string githubToken, string repoName)
    {
        
        // Register a runner with github
        using HttpClient client = new();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {githubToken}");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hetzner-autoscale", "1")); 
            
        HttpResponseMessage response = await client.GetAsync(
            $"https://api.github.com/repos/{repoName}/actions/runners");

        if(response.IsSuccessStatusCode)
        {
            string content = await response.Content.ReadAsStringAsync();
            GitHubRunners responseObject = JsonSerializer.Deserialize<GitHubRunners>(content);
            return responseObject;
        }
        Log.Warning($"Unable to get GH runners for repo: [{response.StatusCode}] {response.ReasonPhrase}");
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
        Log.Warning($"Unable to get GH runner token for org: [{response.StatusCode}] {response.ReasonPhrase}");

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
        Log.Warning($"Unable to get GH runner token for repo: [{response.StatusCode}] {response.ReasonPhrase}");

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
}