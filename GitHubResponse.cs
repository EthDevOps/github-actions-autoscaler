// ReSharper disable InconsistentNaming
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
namespace GithubActionsOrchestrator;

public class GitHubResponse
{
    public string token { get; set; }
    public string expires_at { get; set; }
}