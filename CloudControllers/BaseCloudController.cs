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
using RandomFriendlyNameGenerator;

namespace GithubActionsOrchestrator.CloudControllers;

public class BaseCloudController
{
    protected readonly ILogger _logger;

    private readonly List<MachineSize> _configSizes;
    protected readonly string _provisionBaseUrl;
    protected readonly string _metricUser;
    protected readonly string _metricPassword;

    protected BaseCloudController(ILogger logger,
        List<MachineSize> configSizes,
        string provisionScriptBaseUrl,
        string metricUser,
        string metricPassword)
    {
        _configSizes = configSizes;
        _logger = logger;
        _provisionBaseUrl = provisionScriptBaseUrl;
        _metricUser = metricUser;
        _metricPassword = metricPassword;
    }

    protected string GenerateCloudInit(string targetName, string runnerToken, string size, RunnerProfile profile, bool isCustom,string arch)
    {
        string customEnv = isCustom ? "1" : "0";
        string cloudInitcontent = new StringBuilder()
            .AppendLine("#cloud-config")
            .AppendLine("write_files:")
            .AppendLine("  - path: /data/config.env")
            .AppendLine("    content: |")
            .AppendLine($"      export GH_VERSION='{Program.Config.GithubAgentVersion}'")
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
            .AppendLine($"  - [ sh, -xc, 'curl -fsSL {_provisionBaseUrl}/provision.{profile.ScriptName}.{arch}.v{profile.ScriptVersion}.sh -o /data/provision.sh']")
            .AppendLine("  - [ sh, -xc, 'bash /data/provision.sh']")
            .ToString();
        
        return cloudInitcontent.ToString();

    }
    
    protected async Task<string> GenerateName()
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
    
   protected MachineType SelectMachineType(string arch, string size, string cloudIdentifier, out MachineType machineTypeAlt)
    {
        // Select VM size for job - All AMD 
        List<MachineType> vmSizeTypes = _configSizes.FirstOrDefault(x => x.Arch == arch && x.Name == size)?.VmTypes;
        if (vmSizeTypes == null)
        {
            throw new Exception($"Unknown arch and size combination [{arch}/{size}]");
        }

        MachineType machineType = vmSizeTypes.Where(x => x.Cloud == cloudIdentifier).OrderByDescending(x => x.Priority).FirstOrDefault(); 

        if (machineType == null)
        {
            throw new UnsupportedMachineTypeException($"No Machine type for [{arch}/{size}] on Hetzner");
        }
        
        machineTypeAlt = vmSizeTypes.Where(x => x.VmType != machineType.VmType && x.Cloud == cloudIdentifier).OrderByDescending(x => x.Priority).FirstOrDefault();
        return machineType;
    }

}

