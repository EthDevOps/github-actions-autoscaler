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

    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string WebhookSecret { get; set; }
    
    public int PveTemplate { get; set; }
    
    public int MinVmId { get; set; } = 5000;
    public int ParallelOperations { get; set; } = 10;

    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string DigitalOceanToken { get; set; }

    public List<string> DigitalOceanRegions { get; set; } = new() { "fra1", "ams3", "lon1" };

    public string DigitalOceanTag { get; set; } = "gh-runner";

    public string DigitalOceanVpcUuid { get; set; }

    public string DigitalOceanDefaultImage { get; set; } = "ubuntu-24-04-x64";

    /// <summary>
    /// Teleport-based authorization for mutating dashboard API calls. When disabled,
    /// mutating endpoints fall back to the API key only.
    /// </summary>
    public TeleportAuthConfiguration TeleportAuth { get; set; } = new();
}

public class TeleportAuthConfiguration
{
    /// <summary>Enable JWT verification + role/user authorization on mutating endpoints.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>URL of the Teleport cluster JWKS (e.g. https://teleport.example.com/.well-known/jwks.json).</summary>
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string JwksUrl { get; set; }

    /// <summary>Expected token issuer (the Teleport proxy address). Leave empty to skip issuer validation.</summary>
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string Issuer { get; set; }

    /// <summary>
    /// Expected audience — the public address of the Teleport app that fronts the viewer
    /// (Teleport sets the JWT "aud" to this). Leave empty to skip audience validation.
    /// </summary>
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string Audience { get; set; }

    /// <summary>Teleport roles allowed to run mutating actions. Empty = role membership not required.</summary>
    public List<string> AuthorizedRoles { get; set; } = new();

    /// <summary>Teleport usernames allowed to run mutating actions. Empty = user membership not required.</summary>
    public List<string> AuthorizedUsers { get; set; } = new();
}