using System.Text;
using System.Text.RegularExpressions;
using Corsinvest.ProxmoxVE.Api;
using Corsinvest.ProxmoxVE.Api.Extension;
using GithubActionsOrchestrator.Models;
using HetznerCloudApi.Object.Server;

namespace GithubActionsOrchestrator.CloudControllers;

public class ProxmoxCloudController : BaseCloudController, ICloudController
{
    private readonly List<MachineSize> _configSizes;
    private readonly string _pveHost;
    private readonly string _pveUsername;
    private readonly string _pvePassword;
    private readonly string _mainNode;
    private readonly int _pveTemplate;
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
    private ILogger _logger;

    public ProxmoxCloudController(ILogger<ProxmoxCloudController> logger, List<MachineSize> configSizes, string configProvisionScriptBaseUrl, string configMetricUser, string configMetricPassword, string configPveHost, string configPveUsername, string configPvePassword, int configPveTemplate) :
        base(logger, configSizes, configProvisionScriptBaseUrl, configMetricUser, configMetricPassword)
    {
        _configSizes = configSizes;
        _pveHost = configPveHost;
        _pveUsername = configPveUsername;
        _pvePassword = configPvePassword;
        _pveTemplate = configPveTemplate;
        _mainNode = "colo-pxe-01";
        _logger = logger;
        logger.LogInformation("DCL1 Cloud Controller init done.");
    }

    public async Task<Machine> CreateNewRunner(string arch, string size, string runnerToken, string targetName, bool isCustom = false, string profileName = "default")
    {
        MachineType machineType = SelectMachineType(arch, size, CloudIdentifier, out MachineType machineTypeAlt);
        string hostname = await GenerateName();
        // Load profile info
        RunnerProfile profile = isCustom ? Program.Config.Profiles.FirstOrDefault(x => x.Name == profileName) : Program.Config.Profiles.FirstOrDefault(x => x.Name == "default");

        // Connect to Proxmox API
        var client = new PveClient(_pveHost);

        // Authenticate
        if (!await client.LoginAsync(_pveUsername, _pvePassword))
        {
            throw new Exception("Authentication failed");
        }

        // Source template ID
        int sourceVmId = _pveTemplate;

        // Get next available VMID

        await _semaphore.WaitAsync();

        string macaddress = string.Empty;
        
        
        
        int newVmId;
        try
        {
            
            // Select node
            var resources = (await client.Cluster.Resources.GetAsync("vm")).ToList();
            /*var vmCountByNode = resources
                .Where(x => x.Name.StartsWith(Program.Config.RunnerPrefix))
                .GroupBy(x => x.Node)
                .Select(x => new { Node = x.Key, Count = x.Count()});*/

            var availableNodes = resources.Select(x => x.Node).Distinct().ToList();
            
            var vmCountByNode = availableNodes
                .GroupJoin(
                    resources.Where(x => x.Name.StartsWith(Program.Config.RunnerPrefix)),
                    node => node,
                    resource => resource.Node,
                    (node, matchingResources) => new { Node = node, Count = matchingResources.Count() }
                )
                .ToList(); 
            
            
            var nodeWithLeastRunners = vmCountByNode.OrderBy(x => x.Count).First();
            var selectedNode = nodeWithLeastRunners.Node;
            
            Result newVmIdResult = await client.Cluster.Nextid.Nextid();
            newVmId = int.Parse(newVmIdResult.Response.data);

            // Create linked clone
            var cloneResult = await client.Nodes[_mainNode].Qemu[sourceVmId].Clone.CloneVm(
                newVmId,
                name: hostname,
                description: DateTime.UtcNow.ToString("O"),
                full: false,
                storage: null // Use same storage as source
            );

            if (!cloneResult.IsSuccessStatusCode)
            {
                throw new Exception($"Failed to clone VM: {cloneResult.ReasonPhrase}");
            }

            // Wait for clone task to complete
            await client.WaitForTaskToFinishAsync(cloneResult.Response.data);

            await client.Pools.UpdatePool("github-runners", vms: newVmId.ToString());
            
            
            //await client.Pools.UpdatePool()

            if (_mainNode != selectedNode)
            {
                var migrationResult = await client.Nodes[_mainNode].Qemu[newVmId].Migrate.MigrateVm(selectedNode);
                if (!migrationResult.IsSuccessStatusCode)
                {
                    throw new Exception($"Failed to move VM to target host: {migrationResult.ReasonPhrase}");
                }
                await client.WaitForTaskToFinishAsync(migrationResult.Response.data);
            }
            

            // Set size
            // Pattern to match: digits followed by 'c', then digits followed by 'g'
            var match = System.Text.RegularExpressions.Regex.Match(machineType.VmType, @"(\d+)c(\d+)g");

            int cores = 2;
            int memory = 2;
            if (match.Success && match.Groups.Count >= 3)
            {
                // Parse the captured groups
                if (int.TryParse(match.Groups[1].Value, out int parsedCores))
                {
                    cores = parsedCores;
                }

                if (int.TryParse(match.Groups[2].Value, out int parsedMemory))
                {
                    memory = parsedMemory;
                }
            }

            await client.Nodes[selectedNode].Qemu[newVmId].Config.UpdateVm(
                vcpus: cores,
                memory: (memory * 1024).ToString()
            );

            var vmConfig = await client.Nodes[selectedNode].Qemu[newVmId].Config.VmConfig();
            string netConfig = vmConfig.Response.data.net0;

            // Pattern to match MAC address format after "virtio="
            Regex regex = new("virtio=([0-9A-F]{2}:[0-9A-F]{2}:[0-9A-F]{2}:[0-9A-F]{2}:[0-9A-F]{2}:[0-9A-F]{2})",
                RegexOptions.IgnoreCase);
            Match macMatch = regex.Match(netConfig);

            if (macMatch.Success)
            {
                macaddress = macMatch.Groups[1].Value;
            }
            
            // Start the VM
            var startResult = await client.Nodes[selectedNode].Qemu[newVmId].Status.Start.VmStart();

            if (!startResult.IsSuccessStatusCode)
            {
                throw new Exception($"Failed to start VM: {startResult.ReasonPhrase}");
            }
        }
        finally
        {
            _semaphore.Release();
        }

        string customEnv = isCustom ? "1" : "0";
        string cloudInitScript = new StringBuilder()
            .AppendLine($"export GH_VERSION='{Program.Config.GithubAgentVersion}'")
            .AppendLine($"export ORG_NAME='{targetName}'")
            .AppendLine($"export GH_TOKEN='{runnerToken}'")
            .AppendLine($"export RUNNER_SIZE='{size}'")
            .AppendLine($"export METRIC_USER='{_metricUser}'")
            .AppendLine($"export METRIC_PASS='{_metricPassword}'")
            .AppendLine($"export GH_PROFILE_NAME='{profile.Name}'")
            .AppendLine($"export GH_IS_CUSTOM='{customEnv}'")
            .AppendLine($"export RUNNER_PREFIX='{Program.Config.RunnerPrefix}'")
            .AppendLine($"export CONTROLLER_URL='{Program.Config.ControllerUrl}'")
            .AppendLine($"curl -fsSL {_provisionBaseUrl}/provision.{profile.ScriptName}.{arch}.v{profile.ScriptVersion}.sh -o /data/provision.sh")
            .AppendLine("bash /data/provision.sh")
            .ToString();
        
        
        return new Machine
        {
            Id = newVmId,
            Name = hostname,
            Ipv4 = "0.0.0.0/0",
            CreatedAt = DateTime.UtcNow,
            TargetName = targetName,
            Size = size,
            Arch = arch,
            Profile = profileName,
            IsCustom = isCustom,
            ProvisionId = macaddress.Replace(':','-'),
            ProvisionPayload = cloudInitScript
        };
    }

    public async Task DeleteRunner(long serverId)
    {
        // Connect to Proxmox API
        await _semaphore.WaitAsync();
        try
        {
            var client = new PveClient(_pveHost);

            // Authenticate
            if (!await client.LoginAsync(_pveUsername, _pvePassword))
            {
                throw new Exception("Authentication failed");
            }

            var resources = await client.Cluster.Resources.GetAsync("vm");
            var vms = resources.FirstOrDefault(x => x.VmId == serverId);
            if (vms == null)
            {
               _logger.LogError("Unable to find VM with ID " + serverId + " in Proxmox."); 
                return;
            }
            string selectedNod = vms.Node;
            var stopResult = await client.Nodes[selectedNod].Qemu[serverId].Status.Stop.VmStop();
            // Wait for clone task to complete
            await client.WaitForTaskToFinishAsync(stopResult.Response.data); 
            
            var destroyResult = await client.Nodes[selectedNod].Qemu[serverId].DestroyVm(skiplock: true);
            // Wait for clone task to complete
            await client.WaitForTaskToFinishAsync(destroyResult.Response.data);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<List<CspServer>> GetAllServersFromCsp()
    {
        await _semaphore.WaitAsync();
        try
        {
            // Connect to Proxmox API
            var client = new PveClient(_pveHost);

            // Authenticate
            if (!await client.LoginAsync(_pveUsername, _pvePassword))
            {
                throw new Exception("Authentication failed");
            }

            var resources = await client.Cluster.Resources.GetAsync("vm");
            var vms = resources.Where(x => x.Name.StartsWith(Program.Config.RunnerPrefix));

            List<CspServer> servers = new();

            foreach (var vm in vms)
            {
                var vmInfo = await client.Nodes[vm.Node].Qemu[vm.VmId].Config.VmConfig();
                servers.Add(new CspServer
                {
                    Id = vm.VmId,
                    Name = vm.Name,
                    CreatedAt = vmInfo.Response.data.description
                });
            }
            return servers;
        }
        finally
        {
            _semaphore.Release();
        }

            
    }

    public async Task<int> GetServerCountFromCsp()
    {

        await _semaphore.WaitAsync();
        try
        {
            // Connect to Proxmox API
            var client = new PveClient(_pveHost);

            // Authenticate
            if (!await client.LoginAsync(_pveUsername, _pvePassword))
            {
                throw new Exception("Authentication failed");
            }

            var resources = await client.Cluster.Resources.GetAsync("vm");
            if (resources == null)
            {
                return 0;
            }
            int runnerCount = resources.Count(x => x.Name.StartsWith(Program.Config.RunnerPrefix));
            return runnerCount;
        }
        finally
        {
            _semaphore.Release();
        }

    }

    public string CloudIdentifier => "pve";
}