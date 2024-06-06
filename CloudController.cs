using System.Text;
using System.Text.Json;
using HetznerCloudApi;
using HetznerCloudApi.Object.Image;
using HetznerCloudApi.Object.Server;
using HetznerCloudApi.Object.ServerType;
using HetznerCloudApi.Object.SshKey;
using HetznerCloudApi.Object.Universal;
using Prometheus;
using RandomFriendlyNameGenerator;

namespace GithubActionsOrchestrator;

public class CloudController
{
    private readonly string _persistentPath;
    private readonly HetznerCloudClient _client;
    private readonly ILogger _logger;
    private List<Machine> _activeRunners = new();
    private static readonly Gauge ActiveMachinesCount = Metrics
        .CreateGauge("github_machines_active", "Number of active machines", labelNames: ["org","size"]);

    private readonly List<MachineSize> _configSizes;
    private readonly string _provisionBaseUrl;
    private readonly string _metricUser;
    private readonly string _metricPassword;

    public CloudController(ILogger<CloudController> logger,
        string hetznerCloudToken,
        string persistPath,
        List<MachineSize> configSizes,
        string provisionScriptBaseUrl,
        string metricUser,
        string metricPassword)
    {
        _configSizes = configSizes;
        _persistentPath = Path.Combine(persistPath, "activeRunners.json");
        _client = new(hetznerCloudToken);
        _logger = logger;
        _provisionBaseUrl = provisionScriptBaseUrl;
        _metricUser = metricUser;
        _metricPassword = metricPassword;

        
        _logger.LogInformation("Loading from persistent file.");
        LoadActiveRunners().Wait();
        _logger.LogInformation("Controller init done.");
    }

    public async Task<string> CreateNewRunner(string arch, string size, string runnerToken, string targetName, bool isCustom = false, string profileName = "default")
    {
        
        // Select VM size for job - All AMD 
        string vmSize = _configSizes.FirstOrDefault(x => x.Arch == arch && x.Name == size)?.VmType;

        if (string.IsNullOrEmpty(vmSize))
        {
            throw new Exception($"Unknown arch and size combination [{arch}/{size}]");
        }

        RunnerProfile profile;
        // Build image name
        if (isCustom)
        {
            // Load profile info
            profile = Program.Config.Profiles.FirstOrDefault(x => x.Name == profileName);
        }
        else
        {
            // Load default profile
            profile = Program.Config.Profiles.FirstOrDefault(x => x.Name == "default");
        }

        if (profile == null)
        {
            throw new Exception($"Unable to load profile: {profileName}");
        }
        
        //string imageName = $"gh-actions-img-{arch}-{imageVersion}";
        string imageName = profile.IsCustomImage ? $"ghri-{profile.OsImageName}-{arch}" : profile.OsImageName;
        
       
        // The naming generator might produce names with whitespace.
        string name = $"ghr-{NameGenerator.Identifiers.Get(separator: "-")}".ToLower().Replace(' ','-');
        
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
        
        // Create new server
        string runnerVersion = "2.316.1";
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
            .AppendLine("runcmd:")
            .AppendLine($"  - [ sh, -xc, 'curl -fsSL {_provisionBaseUrl}/provision.{profile.ScriptName}.{arch}.{provisionVersion}.sh -o /data/provision.sh']")
            .AppendLine("  - [ sh, -xc, 'bash /data/provision.sh']")
            .ToString();
        Server newSrv = await _client.Server.Create(eDataCenter.nbg1, imageId.Value, name, srvType.Value, userData: cloudInitcontent, sshKeysIds: srvKeys);
        _activeRunners.Add(new Machine
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
        });
        StoreActiveRunners();
        return newSrv.Name;
    }

    private void StoreActiveRunners()
    {
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(_activeRunners);
            File.WriteAllBytes(_persistentPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Unable to write to {_persistentPath}: {ex.Message}");
        }
        
        var grouped = _activeRunners
            .GroupBy(m => new { OrgName = m.TargetName, m.Size })
            .Select(g => new
            {
                g.Key.OrgName,
                g.Key.Size,
                Count = g.Count()
            })
            .ToList();

        foreach (GithubTargetConfiguration oc in Program.Config.TargetConfigs)
        {
            foreach (MachineSize ms in Program.Config.Sizes)
            {
                int ct = 0;
                var am = grouped.FirstOrDefault(x => x.OrgName == oc.Name && x.Size == ms.Name);
                if (am != null)
                {
                    ct = am.Count;
                }
                ActiveMachinesCount.Labels(oc.Name, ms.Name).Set(ct);
            }
        }
 
    }
    
    public async Task LoadActiveRunners()
    {
        try
        {
            if (!File.Exists(_persistentPath))
            {
                _logger.LogWarning($"No active runner file found at {_persistentPath}");
                return;
            }

            string json = await File.ReadAllTextAsync(_persistentPath);
            List<Machine> restoredRunners = JsonSerializer.Deserialize<List<Machine>>(json);

            if (restoredRunners == null)
            {
                _logger.LogWarning($"Unable to parse active runner file found at {_persistentPath}");
                return;
            }

            _activeRunners = restoredRunners;
            _logger.LogInformation($"Loaded {restoredRunners.Count} runners from store");

            List<Server> htzServers = await _client.Server.Get();

            // Check if known srv are still in hetzner
            foreach (Machine knownSrv in _activeRunners.ToList())
            {
                if (htzServers.All(x => x.Name != knownSrv.Name))
                {
                    // Hetzner server no longer existing - remove from list
                    _logger.LogWarning($"Cleaned {knownSrv.Name} from internal list");
                    _activeRunners.Remove(knownSrv);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Unable to load {_persistentPath}: {ex.Message}");
        }

        StoreActiveRunners();

    }

    public async Task DeleteRunner(long serverId)
    {
        Machine srvMeta = _activeRunners.FirstOrDefault(x => x.Id == serverId);

        if (srvMeta == null)
        {
            _logger.LogWarning($"VM with ID {serverId} not in active runners list. Only removing from htz.");
            await _client.Server.Delete(serverId);
            return;
        }
        
        _logger.LogInformation($"Deleting VM {srvMeta.Name} with IP {srvMeta.Ipv4}");
        await _client.Server.Delete(serverId);
       
        // Do some stats
        Machine vmInfo = _activeRunners.FirstOrDefault(x => x.Id == serverId) ?? throw new InvalidOperationException();

        TimeSpan totalTime = DateTime.UtcNow - vmInfo.CreatedAt;
        TimeSpan runTime = vmInfo.JobPickedUpAt > DateTime.MinValue ? DateTime.UtcNow - vmInfo.JobPickedUpAt : TimeSpan.Zero;
        TimeSpan idleTime = vmInfo.JobPickedUpAt > DateTime.MinValue ? vmInfo.JobPickedUpAt - vmInfo.CreatedAt : DateTime.UtcNow - vmInfo.CreatedAt;
        
        _logger.LogInformation($"VM Stats for {vmInfo.Name} - Total: {totalTime:g} | Setup/Idle: {idleTime:g} | Run: {runTime:g}");
        
        _activeRunners.RemoveAll(x => x.Id == serverId);
        StoreActiveRunners();
    }

    public void AddJobClaimToRunner(string vmId, long jobId, string jobUrl, string repoName)
    {
        Machine vm = _activeRunners.FirstOrDefault(x => x.Name == vmId) ?? throw new InvalidOperationException();
        vm.JobId = jobId;
        vm.JobUrl = jobUrl;
        vm.RepoName = repoName;
        vm.JobPickedUpAt = DateTime.UtcNow;
        StoreActiveRunners();
    }
    
    public Machine GetInfoForJob(long jobId)
    {
        return _activeRunners.FirstOrDefault(x => x.JobId == jobId) ?? null;
    }

    public async Task<List<Server>> GetAllServers()
    {
        List<Server> srvs = await _client.Server.Get();
        return srvs;
    }

    public List<Machine> GetRunnersForTarget(string orgName)
    {
        return _activeRunners.Where(x => x.TargetName == orgName).ToList();
    }

    public Machine GetRunnerByHostname(string hostname)
    {
        return _activeRunners.FirstOrDefault(x => x.Name == hostname);
    }

    public List<Machine> GetAllRunners()
    {
        return _activeRunners.ToList();
    }
}