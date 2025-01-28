using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using GithubActionsOrchestrator.Database;
using GithubActionsOrchestrator.GitHub;
using GithubActionsOrchestrator.Models;
using HetznerCloudApi;
using HetznerCloudApi.Object.Image;
using HetznerCloudApi.Object.Server;
using HetznerCloudApi.Object.ServerType;
using HetznerCloudApi.Object.SshKey;
using HetznerCloudApi.Object.Universal;
using Microsoft.EntityFrameworkCore;
using RandomFriendlyNameGenerator;

namespace GithubActionsOrchestrator;

public class CloudController
{
    private readonly HetznerCloudClient _client;
    private readonly ILogger _logger;

    private readonly List<MachineSize> _configSizes;
    private readonly string _provisionBaseUrl;
    private readonly string _metricUser;
    private readonly string _metricPassword;

    public CloudController(ILogger<CloudController> logger,
        string hetznerCloudToken,
        List<MachineSize> configSizes,
        string provisionScriptBaseUrl,
        string metricUser,
        string metricPassword)
    {
        _configSizes = configSizes;
        _client = new(hetznerCloudToken);
        _logger = logger;
        _provisionBaseUrl = provisionScriptBaseUrl;
        _metricUser = metricUser;
        _metricPassword = metricPassword;
        
        _logger.LogInformation("Controller init done.");
    }

    private async Task<string> GenerateName()
    {
        var db = new ActionsRunnerContext();

        string name = string.Empty;
        bool nameCollision = false;
        do
        {
            if (nameCollision)
            {
                _logger.LogWarning($"Name collision detected: {name}");
            }
            
            // Name duplicate detection. Generate names as long as there is no duplicate found
            name = $"{Program.Config.RunnerPrefix}-{NameGenerator.Identifiers.Get(IdentifierTemplate.AnyThreeComponents,  NameOrderingStyle.BobTheBuilderStyle, separator: "-")}".ToLower().Replace(' ', '-');

            nameCollision = true;
        } while (await db.Runners.AnyAsync(x => x.Hostname == name));

        return name;
    }
    
    public async Task<Machine> CreateNewRunner(string arch, string size, string runnerToken, string targetName, bool isCustom = false, string profileName = "default")
    {
        
        // Select VM size for job - All AMD 
        string vmSize = _configSizes.FirstOrDefault(x => x.Arch == arch && x.Name == size)?.VmType;

        if (string.IsNullOrEmpty(vmSize))
        {
            throw new Exception($"Unknown arch and size combination [{arch}/{size}]");
        }

        // Build image name

        // Load profile info
        RunnerProfile profile = isCustom ? 
            Program.Config.Profiles.FirstOrDefault(x => x.Name == profileName) :
            Program.Config.Profiles.FirstOrDefault(x => x.Name == "default");

        if (profile == null)
        {
            throw new Exception($"Unable to load profile: {profileName}");
        }
        
        string imageName = profile.IsCustomImage ? $"ghri-{profile.OsImageName}-{arch}" : profile.OsImageName;
       
        // The naming generator might produce names with whitespace.
        string name = await GenerateName();
        
        _logger.LogInformation($"Creating VM {name} from image {imageName} of size {size} for {targetName}");

        string htzArch = "x86";
        if (arch == "arm64")
        {
            htzArch = "arm";
        }
        
        // Grab image
        List<Image> images = await _client.Image.Get();
        long? imageId =  images.FirstOrDefault(x => x.Description == imageName && x.Architecture == htzArch)?.Id;

        if (!imageId.HasValue)
        {
            throw new ImageNotFoundException($"Unable to find image: {imageName}");
        }
        
        // Grab server type
        List<ServerType> srvTypes = await _client.ServerType.Get();
        long? srvType = srvTypes.FirstOrDefault(x => x.Name == vmSize)?.Id;
       
        // Grab SSH keys
        List<SshKey> sshKeys = await _client.SshKey.Get();
        List<long> srvKeys = sshKeys.Select(x => x.Id).ToList();
        
        // Grab private network
        var networks = await _client.Network.Get();
        
        // Create new server
        string runnerVersion = Program.Config.GithubAgentVersion;
        string provisionVersion = $"v{profile.ScriptVersion}";
        string customEnv = isCustom ? "1" : "0";
        
        string cloudInitcontent = new StringBuilder()
            .AppendLine("#cloud-config")
            .AppendLine("write_files:")
            .AppendLine("  - path: /data/config.env")
            .AppendLine("    content: |")
            .AppendLine($"      export GH_VERSION='{runnerVersion}'")
            .AppendLine($"      export ORG_NAME='{targetName}'")
            .AppendLine($"      export GH_TOKEN='{runnerToken}'")
            .AppendLine($"      export RUNNER_SIZE='{size}'")
            .AppendLine($"      export METRIC_USER='{_metricUser}'")
            .AppendLine($"      export METRIC_PASS='{_metricPassword}'")
            .AppendLine($"      export GH_PROFILE_NAME='{profile.Name}'")
            .AppendLine($"      export GH_IS_CUSTOM='{customEnv}'")
            .AppendLine($"      export RUNNER_PREFIX='{Program.Config.RunnerPrefix}'")
            .AppendLine($"      export CONTROLLER_URL='{Program.Config.ControllerUrl}'")
            .AppendLine("runcmd:")
            .AppendLine($"  - [ sh, -xc, 'curl -fsSL {_provisionBaseUrl}/provision.{profile.ScriptName}.{arch}.{provisionVersion}.sh -o /data/provision.sh']")
            .AppendLine("  - [ sh, -xc, 'bash /data/provision.sh']")
            .ToString();

        Server newSrv = null;
        bool success = false;
        List<eDataCenter> dataCenters =
        [
            eDataCenter.nbg1,
            eDataCenter.fsn1,
            eDataCenter.hel1
        ];

        int ct = 0;

        while (!success)
        {
            if (ct == dataCenters.Count)
            {
                throw new Exception($"Unable to find any htz DC able to host {name} of size {size}");
            }
            try
            {
                var privateNetworks = profile.UsePrivateNetworks ? networks.Select(x => x.Id).ToList() : new List<long>();
                
                newSrv = await _client.Server.Create(dataCenters[ct], imageId.Value, name, srvType.Value, userData: cloudInitcontent, sshKeysIds: srvKeys, privateNetoworksIds: privateNetworks);
                success = true;
            }
            catch (Exception ex)
            {
                // Htz not able to schedule in nbg, try different one
                if (ex.Message.Contains("resource_unavailable"))
                {
                    _logger.LogWarning($"Unable to create VM {name} from image {imageName} of size {size} in {dataCenters[ct]}. Trying different location.");
                    ct++;
                }
                else
                {
                    throw;
                }

            }
            
        }

        if (newSrv == null)
        {
            throw new Exception("Unable to spin up VM for " + name);
        }

        return new Machine
        {
            Id = newSrv.Id,
            Name = newSrv.Name,
            Ipv4 = newSrv.PublicNet.Ipv4.Ip,
            CreatedAt = DateTime.UtcNow,
            TargetName = targetName,
            Size = size,
            Arch = arch,
            Profile = profileName,
            IsCustom = isCustom
        };
    }

    public async Task DeleteRunner(long serverId)
    {
        var db = new ActionsRunnerContext();
        var runner = await db.Runners.FirstOrDefaultAsync(x => x.CloudServerId == serverId);
        if (runner == null)
        {
            _logger.LogWarning($"VM with ID {serverId} not in active runners list. Only removing from htz.");
        }
        else
        {
            _logger.LogInformation($"Deleting VM {runner.Hostname} with IP {runner.IPv4}");
        }
        
        await _client.Server.Delete(serverId);
       
    }

    public async Task<List<Server>> GetAllServersFromCsp()
    {
        List<Server> srvs = await _client.Server.Get();
        return srvs.Where(x =>x.Name.StartsWith(Program.Config.RunnerPrefix)).ToList();
    }

    public async Task<int> GetServerCountFromCsp()
    {
        return (await GetAllServersFromCsp()).Count;
    }
}