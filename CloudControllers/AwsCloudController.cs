using System.Text;
using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using GithubActionsOrchestrator.Models;

namespace GithubActionsOrchestrator.CloudControllers;

public class AwsCloudController : BaseCloudController, ICloudController
{
    // Canonical's AWS account - owner of the official upstream Ubuntu AMIs
    private const string CanonicalOwnerId = "099720109477";

    // EC2 instance IDs (i-0123456789abcdef0) don't fit in a long, so each instance gets a numeric ID tag
    // that acts as the CloudServerId for the rest of the orchestrator.
    private const string ServerIdTag = "gao-server-id";

    private readonly AmazonEC2Client _client;
    private readonly List<string> _subnetIds;
    private readonly List<string> _securityGroupIds;
    private readonly string _keyName;
    private readonly int _rootVolumeSizeGb;
    private readonly string _defaultImage;
    private new readonly ILogger _logger;

    public AwsCloudController(
        ILogger<AwsCloudController> logger,
        string region,
        string accessKeyId,
        string secretAccessKey,
        List<MachineSize> configSizes,
        string provisionScriptBaseUrl,
        string metricUser,
        string metricPassword,
        List<string> subnetIds,
        List<string> securityGroupIds,
        string keyName,
        int rootVolumeSizeGb,
        string defaultImage)
        : base(logger, configSizes, provisionScriptBaseUrl, metricUser, metricPassword)
    {
        _subnetIds = subnetIds;
        _securityGroupIds = securityGroupIds;
        _keyName = keyName;
        _rootVolumeSizeGb = rootVolumeSizeGb;
        _defaultImage = defaultImage;
        _logger = logger;

        RegionEndpoint endpoint = RegionEndpoint.GetBySystemName(region);
        // Static keys if configured, otherwise the default credential chain (env, IAM role, ...)
        _client = string.IsNullOrWhiteSpace(accessKeyId)
            ? new AmazonEC2Client(endpoint)
            : new AmazonEC2Client(accessKeyId, secretAccessKey, endpoint);

        _logger.LogInformation($"AWS Cloud Controller init done (region {region}).");
    }

    private async Task<Image> ResolveImage(RunnerProfile profile, string arch)
    {
        string awsArch = arch == "arm64" ? "arm64" : "x86_64";
        DescribeImagesRequest request;

        if (profile.IsCustomImage)
        {
            request = new DescribeImagesRequest
            {
                Owners = ["self"],
                Filters = [new Filter("name", [$"ghri-{profile.OsImageName}-{arch}"])]
            };
        }
        else
        {
            // Upstream Ubuntu cloud image, e.g. ubuntu/images/hvm-ssd-gp3/ubuntu-noble-24.04-arm64-server-20250101
            string ubuntuArch = arch == "arm64" ? "arm64" : "amd64";
            string release = MapOsImage(profile.OsImageName) ?? _defaultImage;
            request = new DescribeImagesRequest
            {
                Owners = [CanonicalOwnerId],
                Filters = [new Filter("name", [$"ubuntu/images/*/{release}-{ubuntuArch}-server-*"])]
            };
        }

        request.Filters.Add(new Filter("architecture", [awsArch]));
        request.Filters.Add(new Filter("state", ["available"]));

        var response = await _client.DescribeImagesAsync(request);
        Image image = response.Images?.OrderByDescending(x => x.CreationDate).FirstOrDefault();

        if (image == null)
        {
            throw new ImageNotFoundException($"Unable to find AWS image for profile {profile.Name} ({profile.OsImageName}/{arch})");
        }

        return image;
    }

    private static string MapOsImage(string profileOsImageName)
    {
        switch (profileOsImageName)
        {
            case "Ubuntu 24.04":
                return "ubuntu-noble-24.04";
            case "Ubuntu 22.04":
                return "ubuntu-jammy-22.04";
            default:
                return null;
        }
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

        Image image = await ResolveImage(profile, arch);
        string name = await GenerateName();
        long serverId = Random.Shared.NextInt64(1, long.MaxValue);

        _logger.LogInformation($"Creating AWS instance {name} from image {image.Name} ({image.ImageId}) of size {size} for {targetName}");

        string cloudInitContent = GenerateCloudInit(targetName, runnerToken, size, profile, isCustom, arch);
        string userData = Convert.ToBase64String(Encoding.UTF8.GetBytes(cloudInitContent));

        // Subnet (= AZ) failover loop - mirrors Hetzner DC pattern
        Instance instance = null;
        int subnetIndex = 0;
        string currentVmType = machineType.VmType;
        bool triedAlt = false;

        while (instance == null)
        {
            if (subnetIndex == _subnetIds.Count && machineTypeAlt != null && !triedAlt)
            {
                _logger.LogWarning($"Unable to create AWS instance of type {currentVmType}. Switching to alt size {machineTypeAlt.VmType}...");
                currentVmType = machineTypeAlt.VmType;
                subnetIndex = 0;
                triedAlt = true;
            }
            else if (subnetIndex == _subnetIds.Count)
            {
                if (machineTypeAlt == null)
                {
                    _logger.LogWarning($"No alternative VM types found for {machineType.VmType}");
                }

                throw new Exception($"Unable to find any AWS subnet able to host {name} of size {size}");
            }

            string subnetId = _subnetIds[subnetIndex];

            var request = new RunInstancesRequest
            {
                ImageId = image.ImageId,
                InstanceType = currentVmType,
                MinCount = 1,
                MaxCount = 1,
                UserData = userData,
                KeyName = string.IsNullOrWhiteSpace(_keyName) ? null : _keyName,
                // Runner shutting itself down = gone, no stopped instances lingering around
                InstanceInitiatedShutdownBehavior = ShutdownBehavior.Terminate,
                NetworkInterfaces =
                [
                    new InstanceNetworkInterfaceSpecification
                    {
                        DeviceIndex = 0,
                        SubnetId = subnetId,
                        Groups = _securityGroupIds,
                        AssociatePublicIpAddress = true,
                        DeleteOnTermination = true
                    }
                ],
                // Upstream AMIs ship an 8GB root disk which is too small for runners
                BlockDeviceMappings =
                [
                    new BlockDeviceMapping
                    {
                        DeviceName = image.RootDeviceName,
                        Ebs = new EbsBlockDevice
                        {
                            VolumeSize = _rootVolumeSizeGb,
                            VolumeType = VolumeType.Gp3,
                            DeleteOnTermination = true
                        }
                    }
                ],
                MetadataOptions = new InstanceMetadataOptionsRequest
                {
                    HttpTokens = HttpTokensState.Required
                },
                TagSpecifications =
                [
                    new TagSpecification
                    {
                        ResourceType = ResourceType.Instance,
                        Tags =
                        [
                            new Tag("Name", name),
                            new Tag(ServerIdTag, serverId.ToString())
                        ]
                    }
                ]
            };

            try
            {
                var response = await _client.RunInstancesAsync(request);
                instance = response.Reservation.Instances.First();
            }
            catch (AmazonEC2Exception ex) when (ex.ErrorCode is "InsufficientInstanceCapacity" or "Unsupported")
            {
                SentrySdk.CaptureException(ex, scope =>
                {
                    scope.SetTag("csp", CloudIdentifier);
                    scope.Level = SentryLevel.Warning;
                });
                _logger.LogWarning($"Unable to create AWS instance {name} of type {currentVmType} in {subnetId}: {ex.ErrorCode}. Trying next subnet.");
                subnetIndex++;
            }
        }

        return new Machine
        {
            Id = serverId,
            Name = name,
            // Public IP is assigned after launch; the runner reports it back once provisioned
            Ipv4 = instance.PublicIpAddress ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            TargetName = targetName,
            Size = size,
            Arch = arch,
            Profile = profileName,
            IsCustom = isCustom
        };
    }

    private async Task<List<Instance>> DescribeRunnerInstances(params Filter[] extraFilters)
    {
        var request = new DescribeInstancesRequest
        {
            Filters =
            [
                new Filter("tag-key", [ServerIdTag]),
                new Filter("instance-state-name", ["pending", "running", "stopping", "stopped"]),
                .. extraFilters
            ]
        };

        var instances = new List<Instance>();
        await foreach (var reservation in _client.Paginators.DescribeInstances(request).Reservations)
        {
            instances.AddRange(reservation.Instances ?? []);
        }

        return instances;
    }

    public async Task DeleteRunner(long serverId)
    {
        List<Instance> instances = await DescribeRunnerInstances(new Filter($"tag:{ServerIdTag}", [serverId.ToString()]));
        if (instances.Count == 0)
        {
            _logger.LogInformation($"AWS instance with server id {serverId} not found - already deleted or does not exist");
            return;
        }

        List<string> instanceIds = instances.Select(x => x.InstanceId).ToList();
        _logger.LogInformation($"Terminating AWS instance(s) {string.Join(", ", instanceIds)} (server id {serverId})");
        await _client.TerminateInstancesAsync(new TerminateInstancesRequest { InstanceIds = instanceIds });
    }

    public async Task<List<CspServer>> GetAllServersFromCsp()
    {
        List<Instance> instances = await DescribeRunnerInstances();
        return instances
            .Select(x => new CspServer
            {
                Id = long.Parse(x.Tags.First(t => t.Key == ServerIdTag).Value),
                Name = x.Tags.FirstOrDefault(t => t.Key == "Name")?.Value ?? string.Empty,
                CreatedAt = x.LaunchTime?.ToUniversalTime() ?? DateTime.MinValue
            })
            .Where(x => x.Name.StartsWith(Program.Config.RunnerPrefix))
            .ToList();
    }

    public async Task<int> GetServerCountFromCsp()
    {
        return (await GetAllServersFromCsp()).Count;
    }

    public string CloudIdentifier => "aws";
}
