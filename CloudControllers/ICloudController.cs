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