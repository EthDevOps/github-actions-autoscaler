using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GithubActionsOrchestrator.Models;

namespace GithubActionsOrchestrator.CloudControllers;

public class DigitalOceanCloudController : BaseCloudController, ICloudController
{
    private readonly HttpClient _httpClient;
    private readonly List<MachineSize> _configSizes;
    private readonly List<string> _regions;
    private readonly string _tag;
    private readonly string _vpcUuid;
    private readonly string _defaultImage;
    private new readonly ILogger _logger;

    public DigitalOceanCloudController(
        ILogger<DigitalOceanCloudController> logger,
        string doToken,
        List<MachineSize> configSizes,
        string provisionScriptBaseUrl,
        string metricUser,
        string metricPassword,
        List<string> regions,
        string tag,
        string vpcUuid,
        string defaultImage)
        : base(logger, configSizes, provisionScriptBaseUrl, metricUser, metricPassword)
    {
        _configSizes = configSizes;
        _regions = regions;
        _tag = tag;
        _vpcUuid = vpcUuid;
        _defaultImage = defaultImage;
        _logger = logger;

        _httpClient = new HttpClient();
        _httpClient.BaseAddress = new Uri("https://api.digitalocean.com");
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doToken);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "GithubActionsOrchestrator");

        _logger.LogInformation("DigitalOcean Cloud Controller init done.");
    }

    private async Task<JsonElement> DoApiCall(string endpoint, HttpMethod method = null, object body = null)
    {
        method ??= HttpMethod.Get;

        var request = new HttpRequestMessage(method, endpoint);

        if (body != null && (method == HttpMethod.Post || method == HttpMethod.Put))
        {
            var json = JsonSerializer.Serialize(body);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"DO API {method} {endpoint} failed: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(content))
        {
            return default;
        }

        return JsonSerializer.Deserialize<JsonElement>(content);
    }

    private async Task<List<object>> GetAllSshKeyIds()
    {
        var keys = new List<object>();
        var result = await DoApiCall("/v2/account/keys?per_page=200");
        if (result.TryGetProperty("ssh_keys", out var sshKeys))
        {
            foreach (var key in sshKeys.EnumerateArray())
            {
                keys.Add(key.GetProperty("id").GetInt64());
            }
        }
        return keys;
    }

    private async Task<string> ResolveImageId(string imageName, string arch)
    {
        // If the image looks like a slug (e.g. "ubuntu-24-04-x64"), use it directly
        if (!imageName.StartsWith("ghri-") && !imageName.Contains(' '))
        {
            return imageName;
        }

        // Custom image — resolve via snapshots API
        var result = await DoApiCall("/v2/snapshots?resource_type=droplet&per_page=200");
        if (result.TryGetProperty("snapshots", out var snapshots))
        {
            foreach (var snap in snapshots.EnumerateArray())
            {
                var name = snap.GetProperty("name").GetString();
                if (name == imageName)
                {
                    return snap.GetProperty("id").GetString();
                }
            }
        }

        throw new ImageNotFoundException($"Unable to find DO snapshot: {imageName}");
    }

    public async Task<Machine> CreateNewRunner(string arch, string size, string runnerToken, string targetName,
        bool isCustom = false, string profileName = "default")
    {
        MachineType machineType = SelectMachineType(arch, size, CloudIdentifier, out MachineType machineTypeAlt);

        RunnerProfile profile = isCustom
            ? Program.Config.Profiles.FirstOrDefault(x => x.Name == profileName)
            : Program.Config.Profiles.FirstOrDefault(x => x.Name == "default");

        if (profile == null)
        {
            throw new Exception($"Unable to load profile: {profileName}");
        }

        string imageName = profile.IsCustomImage ? $"ghri-{profile.OsImageName}-{arch}" : (MapOsImage(profile.OsImageName) ?? _defaultImage);
        string imageId = await ResolveImageId(imageName, arch);

        string name = await GenerateName();

        _logger.LogInformation($"Creating DO droplet {name} from image {imageName} of size {size} for {targetName}");

        string cloudInitContent = GenerateCloudInit(targetName, runnerToken, size, profile, isCustom, arch);

        // Grab all SSH keys from the account
        var sshKeyIds = await GetAllSshKeyIds();

        // Region failover loop — mirrors Hetzner pattern
        JsonElement? dropletJson = null;
        bool success = false;
        int regionIndex = 0;
        string currentVmType = machineType.VmType;
        bool triedAlt = false;

        while (!success)
        {
            if (regionIndex == _regions.Count && machineTypeAlt != null && !triedAlt)
            {
                _logger.LogWarning($"Unable to create droplet of type {currentVmType}. Switching to alt size {machineTypeAlt.VmType}...");
                currentVmType = machineTypeAlt.VmType;
                regionIndex = 0;
                triedAlt = true;
            }
            else if (regionIndex == _regions.Count)
            {
                if (machineTypeAlt == null)
                {
                    _logger.LogWarning($"No alternative VM types found for {machineType.VmType}");
                }

                throw new Exception($"Unable to find any DO region able to host {name} of size {size}");
            }

            string region = _regions[regionIndex];

            var createBody = new Dictionary<string, object>
            {
                ["name"] = name,
                ["region"] = region,
                ["size"] = currentVmType,
                ["image"] = imageId,
                ["user_data"] = cloudInitContent,
                ["tags"] = new[] { _tag },
                ["ssh_keys"] = sshKeyIds,
            };

            if (!string.IsNullOrWhiteSpace(_vpcUuid))
            {
                createBody["vpc_uuid"] = _vpcUuid;
            }

            try
            {
                var result = await DoApiCall("/v2/droplets", HttpMethod.Post, createBody);
                dropletJson = result;
                success = true;
            }
            catch (HttpRequestException ex)
            {
                if (ex.Message.Contains("region", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("capacity", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
                {
                    SentrySdk.CaptureException(ex, scope =>
                    {
                        scope.SetTag("csp", "do");
                        scope.Level = SentryLevel.Warning;
                    });
                    _logger.LogWarning($"Unable to create droplet {name} of type {currentVmType} in {region}. Trying next region.");
                    regionIndex++;
                }
                else
                {
                    throw;
                }
            }
        }

        if (dropletJson == null)
        {
            throw new Exception("Unable to spin up droplet for " + name);
        }

        var droplet = dropletJson.Value.GetProperty("droplet");
        long dropletId = droplet.GetProperty("id").GetInt64();

        // The droplet may not have an IP immediately — try to read it
        string ipv4 = string.Empty;
        if (droplet.TryGetProperty("networks", out var networks) &&
            networks.TryGetProperty("v4", out var v4Array))
        {
            foreach (var net in v4Array.EnumerateArray())
            {
                if (net.GetProperty("type").GetString() == "public")
                {
                    ipv4 = net.GetProperty("ip_address").GetString();
                    break;
                }
            }
        }

        return new Machine
        {
            Id = dropletId,
            Name = name,
            Ipv4 = ipv4 ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            TargetName = targetName,
            Size = size,
            Arch = arch,
            Profile = profileName,
            IsCustom = isCustom
        };
    }

    private string MapOsImage(string profileOsImageName)
    {
        switch (profileOsImageName)
        {
            case "Ubuntu 24.04":
                return "ubuntu-24-04-x64";
            default:
                return null;
        }
    }

    public async Task DeleteRunner(long serverId)
    {
        _logger.LogInformation($"Deleting DO droplet {serverId}");

        try
        {
            await DoApiCall($"/v2/droplets/{serverId}", HttpMethod.Delete);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation($"Droplet {serverId} not found - already deleted or does not exist");
        }
    }

    public async Task<List<CspServer>> GetAllServersFromCsp()
    {
        var servers = new List<CspServer>();
        string endpoint = $"/v2/droplets?tag_name={Uri.EscapeDataString(_tag)}&per_page=200";

        while (endpoint != null)
        {
            var result = await DoApiCall(endpoint);

            if (result.TryGetProperty("droplets", out var droplets))
            {
                foreach (var d in droplets.EnumerateArray())
                {
                    string name = d.GetProperty("name").GetString();
                    if (!name.StartsWith(Program.Config.RunnerPrefix))
                    {
                        continue;
                    }

                    long id = d.GetProperty("id").GetInt64();
                    DateTime createdAt = DateTime.MinValue;
                    if (d.TryGetProperty("created_at", out var createdAtEl))
                    {
                        DateTime.TryParse(createdAtEl.GetString(), out createdAt);
                    }

                    servers.Add(new CspServer
                    {
                        Id = id,
                        Name = name,
                        CreatedAt = createdAt
                    });
                }
            }

            // Handle pagination
            endpoint = null;
            if (result.TryGetProperty("links", out var links) &&
                links.TryGetProperty("pages", out var pages) &&
                pages.TryGetProperty("next", out var next))
            {
                string nextUrl = next.GetString();
                if (!string.IsNullOrEmpty(nextUrl))
                {
                    // Extract path + query from the full URL
                    var uri = new Uri(nextUrl);
                    endpoint = uri.PathAndQuery;
                }
            }
        }

        return servers;
    }

    public async Task<int> GetServerCountFromCsp()
    {
        return (await GetAllServersFromCsp()).Count;
    }

    public string CloudIdentifier => "do";
}
