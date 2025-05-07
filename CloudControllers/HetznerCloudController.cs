using System.Text;
using GithubActionsOrchestrator.Database;
using GithubActionsOrchestrator.Models;
using HetznerCloudApi;
using HetznerCloudApi.Object.Image;
using HetznerCloudApi.Object.Server;
using HetznerCloudApi.Object.ServerType;
using HetznerCloudApi.Object.SshKey;
using HetznerCloudApi.Object.Universal;
using Microsoft.EntityFrameworkCore;

namespace GithubActionsOrchestrator.CloudControllers;

public class HetznerCloudController : BaseCloudController, ICloudController
{
    private readonly HetznerCloudClient _client;
    private readonly ILogger _logger;
    private readonly List<MachineSize> _configSizes;
    
    public HetznerCloudController(ILogger<HetznerCloudController> logger,
        string hetznerCloudToken,
        List<MachineSize> configSizes,
        string provisionScriptBaseUrl,
        string metricUser,
        string metricPassword): base(logger, configSizes, provisionScriptBaseUrl, metricUser, metricPassword)
    {
        _configSizes = configSizes;
        _client = new(hetznerCloudToken);
        _logger = logger;
        
        _logger.LogInformation("Hetzner Cloud Controller init done.");
    }

    public async Task<Machine> CreateNewRunner(string arch, string size, string runnerToken, string targetName, bool isCustom = false, string profileName = "default")
    {
        
        MachineType machineType = SelectMachineType(arch, size, CloudIdentifier, out MachineType machineTypeAlt);
        
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
        long? srvType = srvTypes.FirstOrDefault(x => x.Name == machineType.VmType)?.Id;
       
        // Grab SSH keys
        List<SshKey> sshKeys = await _client.SshKey.Get();
        List<long> srvKeys = sshKeys.Select(x => x.Id).ToList();
        
        // Grab private network
        var networks = await _client.Network.Get();
        
        // Create new server
        string cloudInitcontent = GenerateCloudInit(targetName, runnerToken, size, profile, isCustom, arch);
        
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
            if (ct == dataCenters.Count && machineTypeAlt != null)
            {
                long? srvTypeAltId = srvTypes.FirstOrDefault(x => x.Name == machineTypeAlt.VmType)?.Id;
                _logger.LogWarning($"Unable to create VM of type {machineType.VmType}. Switching to alt size {machineTypeAlt.VmType}...");
                srvType = srvTypeAltId;
                ct = 0;
            }
            else if (ct == dataCenters.Count)
            {
                // Select an alternative size
                if (machineTypeAlt == null)
                {
                    _logger.LogWarning($"No alternative VM types found for {machineType.VmType}");
                }
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

    public async Task<List<CspServer>> GetAllServersFromCsp()
    {
        List<Server> srvs = await _client.Server.Get();
        return srvs.Where(x =>x.Name.StartsWith(Program.Config.RunnerPrefix)).Select(x => new CspServer
        {
            Id = x.Id,
            Name = x.Name,
            CreatedAt = x.Created
        }).ToList();
    }

    public async Task<int> GetServerCountFromCsp()
    {
        return (await GetAllServersFromCsp()).Count;
    }

    public string CloudIdentifier => "htz";
}

public class UnsupportedMachineTypeException : Exception
{
    public UnsupportedMachineTypeException()
    {
    }

    public UnsupportedMachineTypeException(string message) : base(message)
    {
    }

    public UnsupportedMachineTypeException(string message, Exception inner) : base(message, inner)
    {
    }
}
