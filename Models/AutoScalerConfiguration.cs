using System.Text.Json.Serialization;
using GithubActionsOrchestrator.GitHub;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace GithubActionsOrchestrator.Models;

public class AutoScalerConfiguration
{
    public List<GithubTargetConfiguration> TargetConfigs { get; set; }
    public List<MachineSize> Sizes { get; set; }

    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string HetznerToken { get; set; }
    
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string RunnerPrefix { get; set; }
    
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string ControllerUrl { get; set; }
    
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string ProvisionScriptBaseUrl { get; set; }

    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string MetricPassword { get; set; }
    
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string MetricUser { get; set; }
    
    public List<RunnerProfile> Profiles { get; set; }

    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string DbConnectionString { get; set; }
    
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string ListenUrl { get; set; }
    
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string GithubAgentVersion { get; set; }
    
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string PvePassword { get; set; }
    
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string PveUsername { get; set; }
    
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string PveHost { get; set; }
    
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string ApiKey { get; set; }
    
    public int PveTemplate { get; set; }
    
    public int MinVmId { get; set; } = 5000;
}