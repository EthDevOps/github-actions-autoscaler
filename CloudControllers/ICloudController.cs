using GithubActionsOrchestrator.Models;
using HetznerCloudApi.Object.Server;

namespace GithubActionsOrchestrator.CloudControllers;

public interface ICloudController
{
    Task<Machine> CreateNewRunner(string arch, string size, string runnerToken, string targetName, bool isCustom = false, string profileName = "default");
    Task DeleteRunner(long serverId);
    Task<List<CspServer>> GetAllServersFromCsp();
    Task<int> GetServerCountFromCsp();
    
    string CloudIdentifier { get; }
}

public class CspServer
{
    public long Id { get; set; }
    public string Name { get; set; }
    public DateTime CreatedAt { get; set; }
}