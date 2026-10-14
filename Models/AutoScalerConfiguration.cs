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

    /// <summary>AWS region, e.g. eu-central-1. AWS controller is only enabled when set.</summary>
    public string AwsRegion { get; set; }

    /// <summary>Optional static credentials. When empty, the default AWS credential chain (env, IAM role, ...) is used.</summary>
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string AwsAccessKeyId { get; set; }

    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string AwsSecretAccessKey { get; set; }

    /// <summary>Subnets (ideally one per AZ) tried in order when capacity is unavailable.</summary>
    public List<string> AwsSubnetIds { get; set; } = new();

    public List<string> AwsSecurityGroupIds { get; set; } = new();

    /// <summary>Optional EC2 key pair name for SSH access.</summary>
    public string AwsKeyName { get; set; }

    public int AwsRootVolumeSizeGb { get; set; } = 100;

    /// <summary>Upstream Ubuntu release used when the profile OS image is not mapped.</summary>
    public string AwsDefaultImage { get; set; } = "ubuntu-noble-24.04";

    /// <summary>
    /// Teleport-based authorization for mutating dashboard API calls. When disabled,
    /// mutating endpoints fall back to the API key only.
    /// </summary>
    public TeleportAuthConfiguration TeleportAuth { get; set; } = new();

    /// <summary>
    /// Warpgate integration for the "hold for inspection" feature. When enabled, holding a
    /// runner registers it as an SSH target in Warpgate and grants the configured access
    /// role access to it. When disabled, holding a runner only protects it from deletion.
    /// </summary>
    public WarpgateConfiguration Warpgate { get; set; } = new();

    /// <summary>
    /// Warpgate-based authorization for mutating dashboard API calls. When enabled, the
    /// caller's identity is taken from the headers Warpgate injects on requests it proxies
    /// to the viewer (X-Warpgate-Username). Takes precedence over <see cref="TeleportAuth"/>
    /// when both are enabled.
    /// </summary>
    public WarpgateAuthConfiguration WarpgateAuth { get; set; } = new();
}

public class WarpgateConfiguration
{
    public bool Enabled { get; set; } = false;

    /// <summary>Base URL of the Warpgate instance, e.g. https://eu.gate.ethdevops.io</summary>
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string BaseUrl { get; set; } = "https://eu.gate.ethdevops.io";

    /// <summary>Admin API token, sent as the X-Warpgate-Token header.</summary>
    [JsonConverter(typeof(EnvironmentAwareJsonConverter<string>))]
    public string ApiKey { get; set; }

    /// <summary>
    /// Name of the Warpgate role that gets access to held runners. The role must already
    /// exist in Warpgate; members of that role can then reach the runner through the gateway.
    /// </summary>
    public string AccessRole { get; set; }

    /// <summary>Username on the runner VM that Warpgate connects as.</summary>
    public string SshUsername { get; set; } = "devops";

    /// <summary>SSH port on the runner VM.</summary>
    public int SshTargetPort { get; set; } = 22;

    /// <summary>
    /// SSH address of the Warpgate gateway for client connections
    /// (ssh &lt;warpgate-user&gt;:&lt;target&gt;@&lt;GatewaySshHost&gt; -p &lt;GatewaySshPort&gt;).
    /// Exposed to the dashboard so operators can copy the connect command.
    /// </summary>
    public string GatewaySshHost { get; set; } = "eu.gate.ethdevops.io";

    public int GatewaySshPort { get; set; } = 22;
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

public class WarpgateAuthConfiguration
{
    /// <summary>Enable Warpgate-identity authorization on mutating endpoints.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Header Warpgate sets with the authenticated user's name on every proxied request.
    /// </summary>
    public string UsernameHeader { get; set; } = "X-Warpgate-Username";

    /// <summary>
    /// Header Warpgate sets with the session type: "user" (logged-in SSO session) or
    /// "ticket" (one-time access link).
    /// </summary>
    public string AuthTypeHeader { get; set; } = "X-Warpgate-Authentication-Types";

    /// <summary>
    /// Warpgate usernames allowed to run mutating actions. Empty = any Warpgate user may
    /// mutate. Warpgate does not forward roles, so authorization is user-based.
    /// </summary>
    public List<string> AuthorizedUsers { get; set; } = new();

    /// <summary>
    /// Require a "user" session for mutating actions. One-time Warpgate ticket URLs act as
    /// the ticket creator, so they are rejected for mutations by default (read-only still works).
    /// </summary>
    public bool RequireUserAuthType { get; set; } = true;
}