namespace GithubActionsOrchestrator;

public class AutoScalerConfiguration
{
    public List<OrgConfiguration> OrgConfigs { get; set; }
    public List<MachineSize> Sizes { get; set; }

    public string HetznerToken { get; set; }
}