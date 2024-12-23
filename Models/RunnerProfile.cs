namespace GithubActionsOrchestrator.Models;

public class RunnerProfile
{
    public string ScriptName { get; set; }
    public int ScriptVersion { get; set; }
    public string OsImageName { get; set; }
    public bool IsCustomImage { get; set; }
    public string Name { get; set; }
    public bool UsePrivateNetworks { get; set; }
}