using System.Text;
using System.Text.Json;
using HetznerCloudApi;
using HetznerCloudApi.Object.ServerType;
using HetznerCloudApi.Object.Universal;
using Prometheus;
using RandomFriendlyNameGenerator;

namespace GithubActionsOrchestrator;

public class CloudController(
    ILogger<CloudController> logger,
    string hetznerCloudToken,
    string persistPath,
    List<MachineSize> configSizes)
{
    private readonly string _persistentPath = Path.Combine(persistPath, "activeRunners.json");
    private readonly HetznerCloudClient _client = new(hetznerCloudToken);
    private readonly ILogger _logger = logger;
    private List<Machine> _activeRunners = new();
    private static readonly Gauge ActiveMachinesCount = Metrics
        .CreateGauge("github_machines_active", "Number of active machines", labelNames: ["org","size"]);

    public async Task<string> CreateNewRunner(string arch, string size, string runnerToken, string orgName)
    {
        
        // Select VM size for job - All AMD 
        string? vmSize = configSizes.FirstOrDefault(x => x.Arch == arch && x.Name == size)?.VmType;

        if (string.IsNullOrEmpty(vmSize))
        {
            throw new Exception($"Unknown arch and size combination [{arch}/{size}]");
        }

        // Build image name
        //string imageName = $"gh-actions-img-{arch}-{imageVersion}";
        string imageName = $"Debian 12";
        string name = $"ghr-{NameGenerator.Identifiers.Get(separator: "-")}".ToLower();
        
        _logger.LogInformation($"Creating VM {name} from image {imageName} of size {size}");
        
        // Grab image
        var images = await _client.Image.Get();
        long? imageId =  images.FirstOrDefault(x => x.Description == imageName)?.Id;

        if (!imageId.HasValue)
        {
            throw new ImageNotFoundException($"Unable to find image: {imageName}");
        }
        
        // Grab server type
        List<ServerType> srvTypes = await _client.ServerType.Get();
        long? srvType = srvTypes.FirstOrDefault(x => x.Name == vmSize)?.Id;
        
        // Create new server
        string runnerVersion = "2.315.0";        
        string cloudInitcontent = new StringBuilder()
            .AppendLine("#cloud-config")
            .AppendLine("runcmd:")
            .AppendLine("  - [ sh, -xc, 'curl -fsSL https://get.docker.com -o /opt/install-docker.sh']")
            .AppendLine("  - [ sh, -xc, 'sh /opt/install-docker.sh']")
            .AppendLine("  - [ sh, -xc, 'mkdir -p /opt/actions-runner']")
            .AppendLine($"  - [ sh, -xc, 'cd /opt/actions-runner && curl -o actions-runner-linux.tar.gz -L https://github.com/actions/runner/releases/download/v{runnerVersion}/actions-runner-linux-x64-{runnerVersion}.tar.gz && tar xzf ./actions-runner-linux.tar.gz']")
            .AppendLine($"  - [ sh, -xc, 'cd /opt/actions-runner && RUNNER_ALLOW_RUNASROOT=true ./config.sh --url https://github.com/{orgName} --token {runnerToken} --ephemeral --disableupdate --labels {size} && RUNNER_ALLOW_RUNASROOT=true ./run.sh ']")
            .ToString();
        _logger.LogInformation($"Launching VM {name}");
        var newSrv = await _client.Server.Create(eDataCenter.nbg1, imageId.Value, name, srvType.Value, userData: cloudInitcontent);
        _activeRunners.Add(new Machine
        {
            Id = newSrv.Id,
            Name = newSrv.Name,
            Ipv4 = newSrv.PublicNet.Ipv4.Ip,
            CreatedAt = DateTime.UtcNow,
            OrgName = orgName,
            Size = size,
            Arch = arch
        });
        StoreActiveRunners();
        _logger.LogInformation($"VM {name} created.");
        return newSrv.Name;
    }

    private void StoreActiveRunners()
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(_activeRunners);
        File.WriteAllBytes(_persistentPath, json);
        
        var grouped = _activeRunners
            .GroupBy(m => new { m.OrgName, m.Size })
            .Select(g => new
            {
                g.Key.OrgName,
                g.Key.Size,
                Count = g.Count()
            })
            .ToList();

        foreach (var group in grouped)
        {
            ActiveMachinesCount.Labels(group.OrgName, group.Size).Set(group.Count);
        }
 
    }
    
    public async Task LoadActiveRunners()
    {
        if (!File.Exists(_persistentPath)) return;
        string json = File.ReadAllText(_persistentPath);
        var restoredRunners = JsonSerializer.Deserialize<List<Machine>>(json);

        if (restoredRunners == null) return;
        _activeRunners = restoredRunners;
        _logger.LogInformation($"Loaded {restoredRunners.Count} runners from store");

        var htzServers = await _client.Server.Get();
        
        // Check if known srv are still in hetzner
        foreach (var knownSrv in _activeRunners.ToList())
        {
            if (htzServers.All(x => x.Name != knownSrv.Name))
            {
                // Hetzner server no longer existing - remove from list
                _logger.LogWarning($"Cleaned {knownSrv.Name} from internal list");
                _activeRunners.Remove(knownSrv);
            }
        }
        StoreActiveRunners();

    }

    public async Task DeleteRunner(long serverId)
    {
        var srvMeta = _activeRunners.FirstOrDefault(x => x.Id == serverId);

        if (srvMeta == null)
        {
            _logger.LogError($"VM with ID {serverId} not in list. Aborting.");
            return;
        }
        
        _logger.LogInformation($"Deleting VM {srvMeta.Name} with IP {srvMeta.Ipv4}");
        var srv = await _client.Server.Get(serverId);
        await _client.Server.Delete(serverId);
       
        // Do some stats
        var vmInfo = _activeRunners.FirstOrDefault(x => x.Id == serverId) ?? throw new InvalidOperationException();

        var totalTime = DateTime.UtcNow - vmInfo.CreatedAt;
        var runTime = DateTime.UtcNow - vmInfo.JobPickedUpAt;
        var setupTime = vmInfo.JobPickedUpAt - vmInfo.CreatedAt;
        
        _logger.LogInformation($"VM Stats for {vmInfo.Name} - Total: {totalTime:g} | Setup: {setupTime:g} | Run: {runTime:g}");
        
        _activeRunners.RemoveAll(x => x.Id == serverId);
        StoreActiveRunners();
    }

    public void AddJobClaimToRunner(string? vmId, long jobId, string? jobUrl, string? repoName)
    {
        Machine vm = _activeRunners.FirstOrDefault(x => x.Name == vmId) ?? throw new InvalidOperationException();
        vm.JobId = jobId;
        vm.JobUrl = jobUrl;
        vm.RepoName = repoName;
        vm.JobPickedUpAt = DateTime.UtcNow;
        StoreActiveRunners();
    }
    
    public Machine? GetInfoForJob(long jobId)
    {
        return _activeRunners.FirstOrDefault(x => x.JobId == jobId) ?? null;
    }

    public List<Machine> GetRunnersForOrg(string orgName)
    {
        return _activeRunners.Where(x => x.OrgName == orgName).ToList();
    }
}