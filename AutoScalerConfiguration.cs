namespace GithubActionsOrchestrator;

public class AutoScalerConfiguration
{
    public List<OrgConfiguration> OrgConfigs { get; set; }
    public List<MachineSize> Sizes { get; set; }

    public string HetznerToken { get; set; }
    public string ProvisionScriptBaseUrl { get; set; }
    public string MetricPassword { get; set; }
    public string MetricUser { get; set; }
}