namespace GithubActionsOrchestrator.Models;

public class MachineSize
{
    public string Name { get; set; }
    public string Arch { get; set; }
    public List<MachineType> VmTypes { get; set; }

}