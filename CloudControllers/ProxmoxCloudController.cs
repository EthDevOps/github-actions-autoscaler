using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private readonly int _minVmId;
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
    private readonly HttpClient _httpClient;
    private new readonly ILogger _logger;
    private string _authTicket;
    private string _csrfToken;
    private DateTime _authExpiration = DateTime.MinValue;

    public ProxmoxCloudController(ILogger<ProxmoxCloudController> logger, List<MachineSize> configSizes, string configProvisionScriptBaseUrl, string configMetricUser, string configMetricPassword, string configPveHost, string configPveUsername, string configPvePassword, int configPveTemplate, int minVmId = 5000) :
        base(logger, configSizes, configProvisionScriptBaseUrl, configMetricUser, configMetricPassword)
    {
        _configSizes = configSizes;
        _pveHost = configPveHost;
        _pveUsername = configPveUsername;
        _pvePassword = configPvePassword;
        _pveTemplate = configPveTemplate;
        _minVmId = minVmId;
        _mainNode = "colo-pxe-01";
        _logger = logger;
        
        // Validate configuration for safety
        ValidateConfiguration();
        
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "GithubActionsOrchestrator");
        var handler = new HttpClientHandler()
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
        };
        _httpClient = new HttpClient(handler);
        
        logger.LogInformation("PVE Cloud Controller init done.");
    }

    private void ValidateConfiguration()
    {
        if (_minVmId < 1000)
        {
            throw new ArgumentException($"MinVmId ({_minVmId}) must be >= 1000 for safety");
        }

        if (string.IsNullOrEmpty(_pveHost))
        {
            throw new ArgumentException("PVE Host cannot be null or empty");
        }

        if (string.IsNullOrEmpty(_pveUsername) || string.IsNullOrEmpty(_pvePassword))
        {
            throw new ArgumentException("PVE credentials cannot be null or empty");
        }

        _logger.LogInformation($"PVE Controller configured with MinVmId: {_minVmId}, Host: {_pveHost}");
    }

    private async Task<bool> AuthenticateAsync()
    {
        if (_authTicket != null && DateTime.UtcNow < _authExpiration)
        {
            return true;
        }

        try
        {
            var authData = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("username", _pveUsername),
                new KeyValuePair<string, string>("password", _pvePassword)
            });

            var response = await _httpClient.PostAsync($"https://{_pveHost}:8006/api2/json/access/ticket", authData);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Authentication failed: {response.StatusCode}");
                return false;
            }

            var content = await response.Content.ReadAsStringAsync();
            var authResponse = JsonSerializer.Deserialize<JsonElement>(content);
            
            if (authResponse.TryGetProperty("data", out var dataElement))
            {
                _authTicket = dataElement.GetProperty("ticket").GetString();
                _csrfToken = dataElement.GetProperty("CSRFPreventionToken").GetString();
                _authExpiration = DateTime.UtcNow.AddHours(1.5);
                
                _httpClient.DefaultRequestHeaders.Remove("Cookie");
                _httpClient.DefaultRequestHeaders.Add("Cookie", $"PVEAuthCookie={_authTicket}");
                
                return true;
            }
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex, scope =>
            {
                scope.SetTag("csp", "pve");
            });
            _logger.LogError(ex, "Authentication failed");
        }

        return false;
    }

    private async Task<string> MakeApiCallAsync(string endpoint, HttpMethod method = null, object data = null)
    {
        method ??= HttpMethod.Get;
        
        if (!await AuthenticateAsync())
        {
            throw new Exception("Authentication failed");
        }

        var request = new HttpRequestMessage(method, $"https://{_pveHost}:8006/api2/json{endpoint}");
        
        if (method != HttpMethod.Get && _csrfToken != null)
        {
            request.Headers.Add("CSRFPreventionToken", _csrfToken);
        }

        if (data != null && (method == HttpMethod.Post || method == HttpMethod.Put))
        {
            if (data is FormUrlEncodedContent formContent)
            {
                request.Content = formContent;
            }
            else
            {
                var json = JsonSerializer.Serialize(data);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        }

        var response = await _httpClient.SendAsync(request);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new Exception($"API call failed: {response.StatusCode} - {errorContent}");
        }

        return await response.Content.ReadAsStringAsync();
    }

    private async Task<int> GetNextAvailableVmIdAsync()
    {
        var response = await MakeApiCallAsync("/cluster/resources?type=vm");
        var resourcesJson = JsonSerializer.Deserialize<JsonElement>(response);
        
        var existingIds = new HashSet<int>();
        
        if (resourcesJson.TryGetProperty("data", out var dataArray))
        {
            foreach (var resource in dataArray.EnumerateArray())
            {
                if (resource.TryGetProperty("vmid", out var vmidElement))
                {
                    existingIds.Add(vmidElement.GetInt32());
                }
            }
        }

        for (int id = _minVmId; id < _minVmId + 10000; id++)
        {
            if (!existingIds.Contains(id))
            {
                // Additional safety check - log the allocated VM ID
                _logger.LogInformation($"Allocated VM ID: {id} (range: {_minVmId}-{_minVmId + 10000})");
                return id;
            }
        }

        throw new Exception($"No available VM IDs found in range {_minVmId}-{_minVmId + 10000}");
    }

    private async Task<string> WaitForTaskCompletionAsync(string taskId, string node)
    {
        while (true)
        {
            var response = await MakeApiCallAsync($"/nodes/{node}/tasks/{taskId}/status");
            var taskJson = JsonSerializer.Deserialize<JsonElement>(response);
            
            if (taskJson.TryGetProperty("data", out var taskData))
            {
                if (taskData.TryGetProperty("status", out var statusElement))
                {
                    var status = statusElement.GetString();
                    if (status == "stopped")
                    {
                        if (taskData.TryGetProperty("exitstatus", out var exitStatus))
                        {
                            if (exitStatus.GetString() != "OK")
                            {
                                throw new Exception($"Task failed with exit status: {exitStatus.GetString()}");
                            }
                        }
                        return status;
                    }
                }
            }
            
            await Task.Delay(500);
        }
    }

    private async Task<string> GetVmIpAddressAsync(int vmId, string node, int timeoutSeconds = 120)
    {
        var endTime = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        
        while (DateTime.UtcNow < endTime)
        {
            try
            {
                var response = await MakeApiCallAsync($"/nodes/{node}/qemu/{vmId}/agent/network-get-interfaces");
                var networkJson = JsonSerializer.Deserialize<JsonElement>(response);
                
                if (networkJson.TryGetProperty("data", out var dataElement) && dataElement.TryGetProperty("result", out var interfaces))
                {
                    foreach (var iface in interfaces.EnumerateArray())
                    {
                        if (iface.TryGetProperty("name", out var nameElement) && 
                            nameElement.GetString() != "lo" && 
                            iface.TryGetProperty("ip-addresses", out var ipAddresses))
                        {
                            foreach (var ipAddr in ipAddresses.EnumerateArray())
                            {
                                if (ipAddr.TryGetProperty("ip-address-type", out var typeElement) &&
                                    typeElement.GetString() == "ipv4" &&
                                    ipAddr.TryGetProperty("ip-address", out var ipElement))
                                {
                                    var ip = ipElement.GetString();
                                    if (!ip.StartsWith("169.254.") && !ip.StartsWith("127."))
                                    {
                                        _logger.LogInformation($"Retrieved IP address for VM {vmId}: {ip}");
                                        return ip;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Failed to get IP for VM {vmId}, retrying... Error: {ex.Message}");
            }
            
            await Task.Delay(3000);
        }
        
        _logger.LogWarning($"Timeout waiting for IP address for VM {vmId}, falling back to dummy IP");
        return "0.0.0.0/0";
    }

    public async Task<string> UpdateRunnerIpAddressAsync(long cloudServerId)
    {
        await _semaphore.WaitAsync();
        try
        {
            var resourcesResponse = await MakeApiCallAsync("/cluster/resources?type=vm");
            var resourcesJson = JsonSerializer.Deserialize<JsonElement>(resourcesResponse);
            
            string selectedNode = null;
            
            if (resourcesJson.TryGetProperty("data", out var dataArray))
            {
                foreach (var resource in dataArray.EnumerateArray())
                {
                    if (resource.TryGetProperty("vmid", out var vmidElement) && 
                        vmidElement.GetInt32() == cloudServerId)
                    {
                        selectedNode = resource.GetProperty("node").GetString();
                        break;
                    }
                }
            }
            
            if (selectedNode == null)
            {
                _logger.LogWarning($"Unable to find VM with ID {cloudServerId} in Proxmox for IP update");
                return "0.0.0.0/0";
            }

            return await GetVmIpAddressAsync((int)cloudServerId, selectedNode);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<Machine> CreateNewRunner(string arch, string size, string runnerToken, string targetName, bool isCustom = false, string profileName = "default")
    {
        MachineType machineType = SelectMachineType(arch, size, CloudIdentifier, out MachineType machineTypeAlt);
        string hostname = await GenerateName();
        RunnerProfile profile = isCustom ? Program.Config.Profiles.FirstOrDefault(x => x.Name == profileName) : Program.Config.Profiles.FirstOrDefault(x => x.Name == "default");

        int sourceVmId = _pveTemplate;

        string macaddress = string.Empty;
        int newVmId;
        string selectedNode = _mainNode;

        // Try to find the node with the least runners for load balancing
        try
        {
            var resourcesResponse = await MakeApiCallAsync("/cluster/resources?type=vm");
            var resourcesJson = JsonSerializer.Deserialize<JsonElement>(resourcesResponse);

            if (resourcesJson.TryGetProperty("data", out var dataArray))
            {
                var resources = dataArray.EnumerateArray().ToList();
                var availableNodes = resources
                    .Where(r => r.TryGetProperty("node", out var _))
                    .Select(r => r.GetProperty("node").GetString())
                    .Distinct().ToList();

                var vmCountByNode = availableNodes
                    .Select(node => new
                    {
                        Node = node,
                        Count = resources.Count(r =>
                            r.TryGetProperty("node", out var nodeElement) &&
                            r.TryGetProperty("name", out var nameElement) &&
                            nodeElement.GetString() == node &&
                            nameElement.GetString().StartsWith(Program.Config.RunnerPrefix))
                    })
                    .ToList();

                var nodeWithLeastRunners = vmCountByNode.OrderBy(x => x.Count).First();
                selectedNode = nodeWithLeastRunners.Node;
            }
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex, scope =>
            {
                scope.SetTag("csp", "pve");
            });
            _logger.LogError(ex, "Unable to get available nodes - using main node");
            await Task.Delay(500);
        }

        // Serialize VM cloning to prevent VMID collisions
        await _semaphore.WaitAsync();
        try
        {
            newVmId = await GetNextAvailableVmIdAsync();

            var cloneData = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("newid", newVmId.ToString()),
                new KeyValuePair<string, string>("name", hostname),
                new KeyValuePair<string, string>("description", DateTime.UtcNow.ToString("O")),
                new KeyValuePair<string, string>("full", "0")
            });

            var cloneResponse = await MakeApiCallAsync($"/nodes/{_mainNode}/qemu/{sourceVmId}/clone", HttpMethod.Post, cloneData);
            var cloneJson = JsonSerializer.Deserialize<JsonElement>(cloneResponse);

            if (cloneJson.TryGetProperty("data", out var cloneTaskId))
            {
                await WaitForTaskCompletionAsync(cloneTaskId.GetString(), _mainNode);
            }
        }
        finally
        {
            _semaphore.Release();
        }

        var poolData = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("vms", newVmId.ToString())
        });
        await MakeApiCallAsync("/pools/github-runners", HttpMethod.Put, poolData);

        if (_mainNode != selectedNode)
        {
            var migrateData = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("target", selectedNode)
            });

            var migrateResponse = await MakeApiCallAsync($"/nodes/{_mainNode}/qemu/{newVmId}/migrate", HttpMethod.Post, migrateData);
            var migrateJson = JsonSerializer.Deserialize<JsonElement>(migrateResponse);

            if (migrateJson.TryGetProperty("data", out var migrateTaskId))
            {
                await WaitForTaskCompletionAsync(migrateTaskId.GetString(), _mainNode);
            }
        }

        var match = Regex.Match(machineType.VmType, @"(\d+)c(\d+)g");
        int cores = 2;
        int memory = 2;

        if (match.Success && match.Groups.Count >= 3)
        {
            if (int.TryParse(match.Groups[1].Value, out int parsedCores))
            {
                cores = parsedCores;
            }

            if (int.TryParse(match.Groups[2].Value, out int parsedMemory))
            {
                memory = parsedMemory;
            }
        }

        var configData = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("cores", cores.ToString()),
            new KeyValuePair<string, string>("memory", (memory * 1024).ToString())
        });

        await MakeApiCallAsync($"/nodes/{selectedNode}/qemu/{newVmId}/config", HttpMethod.Put, configData);

        var vmConfigResponse = await MakeApiCallAsync($"/nodes/{selectedNode}/qemu/{newVmId}/config");
        var vmConfigJson = JsonSerializer.Deserialize<JsonElement>(vmConfigResponse);

        if (vmConfigJson.TryGetProperty("data", out var configData2) &&
            configData2.TryGetProperty("net0", out var net0Element))
        {
            string netConfig = net0Element.GetString();

            Regex regex = new("virtio=([0-9A-F]{2}:[0-9A-F]{2}:[0-9A-F]{2}:[0-9A-F]{2}:[0-9A-F]{2}:[0-9A-F]{2})", RegexOptions.IgnoreCase);
            Match macMatch = regex.Match(netConfig);

            if (macMatch.Success)
            {
                macaddress = macMatch.Groups[1].Value;
            }
        }

        var startResponse = await MakeApiCallAsync($"/nodes/{selectedNode}/qemu/{newVmId}/status/start", HttpMethod.Post);
        var startJson = JsonSerializer.Deserialize<JsonElement>(startResponse);

        if (startJson.TryGetProperty("data", out var startTaskId))
        {
            await WaitForTaskCompletionAsync(startTaskId.GetString(), selectedNode);
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
        await _semaphore.WaitAsync();
        try
        {
            var resourcesResponse = await MakeApiCallAsync("/cluster/resources?type=vm");
            var resourcesJson = JsonSerializer.Deserialize<JsonElement>(resourcesResponse);
            
            string selectedNode = null;
            
            if (resourcesJson.TryGetProperty("data", out var dataArray))
            {
                foreach (var resource in dataArray.EnumerateArray())
                {
                    if (resource.TryGetProperty("vmid", out var vmidElement) && 
                        vmidElement.GetInt32() == serverId)
                    {
                        selectedNode = resource.GetProperty("node").GetString();
                        break;
                    }
                }
            }
            
            if (selectedNode == null)
            {
                _logger.LogError("Unable to find VM with ID " + serverId + " in Proxmox.");
                return;
            }

            var stopResponse = await MakeApiCallAsync($"/nodes/{selectedNode}/qemu/{serverId}/status/stop", HttpMethod.Post);
            var stopJson = JsonSerializer.Deserialize<JsonElement>(stopResponse);
            
            if (stopJson.TryGetProperty("data", out var stopTaskId))
            {
                await WaitForTaskCompletionAsync(stopTaskId.GetString(), selectedNode);
            }

            var destroyData = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("skiplock", "1")
            });
            
            var destroyResponse = await MakeApiCallAsync($"/nodes/{selectedNode}/qemu/{serverId}", HttpMethod.Delete, destroyData);
            var destroyJson = JsonSerializer.Deserialize<JsonElement>(destroyResponse);
            
            if (destroyJson.TryGetProperty("data", out var destroyTaskId))
            {
                await WaitForTaskCompletionAsync(destroyTaskId.GetString(), selectedNode);
            }
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
            var resourcesResponse = await MakeApiCallAsync("/cluster/resources?type=vm");
            var resourcesJson = JsonSerializer.Deserialize<JsonElement>(resourcesResponse);
            
            List<CspServer> servers = new();

            if (resourcesJson.TryGetProperty("data", out var dataArray))
            {
                foreach (var resource in dataArray.EnumerateArray())
                {
                    if (resource.TryGetProperty("name", out var nameElement) && 
                        nameElement.GetString().StartsWith(Program.Config.RunnerPrefix))
                    {
                        var vmid = resource.GetProperty("vmid").GetInt32();
                        var name = nameElement.GetString();
                        var node = resource.GetProperty("node").GetString();
                        
                        try
                        {
                            var vmConfigResponse = await MakeApiCallAsync($"/nodes/{node}/qemu/{vmid}/config");
                            var vmConfigJson = JsonSerializer.Deserialize<JsonElement>(vmConfigResponse);
                            
                            DateTime createdAt = DateTime.MinValue;
                            if (vmConfigJson.TryGetProperty("data", out var configData) && 
                                configData.TryGetProperty("description", out var descElement))
                            {
                                var descString = descElement.GetString();
                                if (DateTime.TryParse(descString, out var parsedDate))
                                {
                                    createdAt = parsedDate;
                                }
                            }

                            servers.Add(new CspServer
                            {
                                Id = vmid,
                                Name = name,
                                CreatedAt = createdAt
                            });
                        }
                        catch (Exception ex)
                        {
                            SentrySdk.CaptureException(ex, scope =>
                            {
                                scope.SetTag("csp", "pve");
                            });
                            _logger.LogWarning(ex, $"Failed to get config for VM {vmid}");
                            servers.Add(new CspServer
                            {
                                Id = vmid,
                                Name = name,
                                CreatedAt = DateTime.MinValue
                            });
                        }
                    }
                }
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
            var resourcesResponse = await MakeApiCallAsync("/cluster/resources?type=vm");
            var resourcesJson = JsonSerializer.Deserialize<JsonElement>(resourcesResponse);
            
            if (!resourcesJson.TryGetProperty("data", out var dataArray))
            {
                return 0;
            }
            
            int runnerCount = 0;
            foreach (var resource in dataArray.EnumerateArray())
            {
                if (resource.TryGetProperty("name", out var nameElement) && 
                    nameElement.GetString().StartsWith(Program.Config.RunnerPrefix))
                {
                    runnerCount++;
                }
            }
            
            return runnerCount;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public string CloudIdentifier => "pve";

}