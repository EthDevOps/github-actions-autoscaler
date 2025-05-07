using System.Text.Json.Serialization;
using GithubActionsOrchestrator.Models;

namespace GithubActionsOrchestrator.GitHub;

public class GithubTargetConfiguration
{
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string Name { get; set; }
    
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string GitHubToken { get; set; }
    
    public List<Pool> Pools { get; set; }
    public TargetType Target { get; set; }
}