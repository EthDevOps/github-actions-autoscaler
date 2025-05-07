using GithubActionsOrchestrator.GitHub;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace GithubActionsOrchestrator.Models;

public class AutoScalerConfiguration
{
    public List<GithubTargetConfiguration> TargetConfigs { get; set; }
    public List<MachineSize> Sizes { get; set; }

    public string HetznerToken { get; set; }
    public string RunnerPrefix { get; set; }
    public string ControllerUrl { get; set; }
    public string ProvisionScriptBaseUrl { get; set; }
    public string MetricPassword { get; set; }
    public string MetricUser { get; set; }
    public List<RunnerProfile> Profiles { get; set; }

    public string DbConnectionString { get; set; }
    public string ListenUrl { get; set; }
    public string GithubAgentVersion { get; set; }
    public string PvePassword { get; set; }
    public string PveUsername { get; set; }
    public string PveHost { get; set; }
    public string ApiKey { get; set; }
    public int PveTemplate { get; set; }
}