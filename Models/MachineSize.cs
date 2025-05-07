namespace GithubActionsOrchestrator.Models;

public class MachineSize
{
    public string Name { get; set; }
    public string Arch { get; set; }
    public List<MachineType> VmTypes { get; set; }

}

public class MachineType
{
    public string Cloud { get; set; }
    public string VmType { get; set; }
    public int Priority { get; set; }
}