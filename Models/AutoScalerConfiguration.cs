using GithubActionsOrchestrator.GitHub;

namespace GithubActionsOrchestrator.Models;

public class AutoScalerConfiguration
{
    public List<GithubTargetConfiguration> TargetConfigs { get; set; }
    public List<MachineSize> Sizes { get; set; }

    public string HetznerToken { get; set; }
    public string ProvisionScriptBaseUrl { get; set; }
    public string MetricPassword { get; set; }
    public string MetricUser { get; set; }
    public List<RunnerProfile> Profiles { get; set; }
}