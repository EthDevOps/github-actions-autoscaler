using HetznerCloudApi.Object.Server;
using Prometheus;
using Serilog;

namespace GithubActionsOrchestrator;

public class PoolManager : BackgroundService
{
    private readonly CloudController _cc;
    private readonly ILogger<PoolManager> _logger;
    private static readonly Counter MachineCreatedCount = Metrics
        .CreateCounter("github_machines_created", "Number of created machines", labelNames: ["org","size"]);
    private static readonly Gauge QueueSize = Metrics
        .CreateGauge("github_queue", "Number of queued runner tasks");
    private static readonly Gauge GithubRunnersGauge = Metrics
        .CreateGauge("github_registered_runners", "Number of runners registered to github actions", labelNames: ["org", "status"]);

    private readonly RunnerQueue _queues;


    public PoolManager(CloudController cc, ILogger<PoolManager> logger, RunnerQueue queues)
    {
        _cc = cc;
        _logger = logger;
        _queues = queues;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do init shit
        _logger.LogInformation("PoolManager online.");
        _logger.LogInformation("Queuing base load runner start...");

        var orgConfig = Program.Config.OrgConfigs;
        
        // Cull runners
        List<Server> allHtzSrvs = await _cc.GetAllServers();

        await CullRunners(orgConfig, allHtzSrvs);
        await StartPoolRunners(orgConfig);
        _logger.LogInformation("Poolmanager init done.");

        // Kick the PoolManager into background
        await Task.Yield();
       
        DateTime crudeTimer = DateTime.UtcNow;
        int cullMinutes = 10;
        
        while (!stoppingToken.IsCancellationRequested)
        {
            // Grab some stats
            QueueSize.Set(_queues.CreateTasks.Count + _queues.DeleteTasks.Count);

            try
            {
                foreach (var org in orgConfig)
                {
                    GithubRunnersGauge.Labels(org.OrgName, "active").Set(0);
                    GithubRunnersGauge.Labels(org.OrgName, "idle").Set(0);
                    GithubRunnersGauge.Labels(org.OrgName, "offline").Set(0);
                    var orgRunners = await GitHubApi.GetRunners(org.GitHubToken, org.OrgName);
                    var ghStatus = orgRunners.runners.Where(x => x.name.StartsWith("ghr")).GroupBy(x =>
                    {
                        if (x.busy)
                        {
                            return "active";
                        }

                        if (x.status == "online")
                        {
                            return "idle";
                        }

                        return x.status;
                    }).Select(x => new { Status = x.Key, Count = x.Count() });
                    foreach (var ghs in ghStatus)
                    {
                        GithubRunnersGauge.Labels(org.OrgName, ghs.Status).Set(ghs.Count);
                    }

                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Unable to get stats: {ex.Message}");
            }


            // check for culling interval
            if (DateTime.UtcNow - crudeTimer > TimeSpan.FromMinutes(cullMinutes))
            {
                _logger.LogInformation("Culling runners...");
                await CullRunners(orgConfig, allHtzSrvs);
                await StartPoolRunners(orgConfig);
                crudeTimer = DateTime.UtcNow;
            }
           
            
            if (_queues.DeleteTasks.TryDequeue(out DeleteRunnerTask? dtask))
            {
                _logger.LogInformation($"Current Queue length: C:{_queues.CreateTasks.Count} D:{_queues.DeleteTasks.Count}");
                if (dtask != null)
                {
                    bool success = await DeleteRunner(dtask);
                    if (!success)
                    {
                        // Deletion didn't succeed. Let's hold processing runners for a minute
                        _logger.LogWarning("Encountered a problem deleting runners. Will hold queue processing for 1 minute.");
                        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    }
                }
            }
            
            if (_queues.CreateTasks.TryDequeue(out CreateRunnerTask? task))
            {
                _logger.LogInformation($"Current Queue length: C:{_queues.CreateTasks.Count} D:{_queues.DeleteTasks.Count}");
                if (task != null)
                {
                    bool success = await CreateRunner(task);
                    if (!success)
                    {
                        // Creation didn't succeed. Let's hold of creating new runners for a minute
                        _logger.LogWarning("Encountered a problem creating runners. Will hold queue processing for 1 minute.");
                        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    }
                }
            }

            await Task.Delay(1000, stoppingToken);
        }
    }

    private async Task StartPoolRunners(List<OrgConfiguration> orgConfig)
    {
        // Start pool runners
        foreach (var org in orgConfig)
        {
            _logger.LogInformation($"Checking pool runners for {org.OrgName}");
            string runnerToken = await GitHubApi.GetRunnerToken(org.GitHubToken, org.OrgName);
            List<Machine> existingRunners = _cc.GetRunnersForOrg(org.OrgName);
            
            foreach (var pool in org.Pools)
            {
                int existCt = existingRunners.Count(x => x.Size == pool.Size);
                int missingCt = pool.NumRunners - existCt;

                string arch = Program.Config.Sizes.FirstOrDefault(x => x.Name == pool.Size).Arch;
                
                for (int i = 0; i < missingCt; i++)
                {
                    // Queue VM creation
                    _queues.CreateTasks.Enqueue(new CreateRunnerTask
                    {
                        Arch = arch,
                        Size = pool.Size,
                        RunnerToken = runnerToken,
                        OrgName = org.OrgName
                    }); 
                    _logger.LogInformation($"[{i+1}/{missingCt}] Queued {pool.Size} runner for {org.OrgName}");
                }
            }
        }
    }

    private async Task CullRunners(List<OrgConfiguration> orgConfig, List<Server> allHtzSrvs)
    {
        List<string> registeredServerNames = new List<string>();
        foreach (OrgConfiguration org in orgConfig)
        {
            _logger.LogInformation($"Culling runners for {org.OrgName}...");

            // Get runner infos
            GitHubRunners githubRunners = await GitHubApi.GetRunners(org.GitHubToken, org.OrgName);
           
            // Remove all offline runner entries from GitHub
            List<GitHubRunner> ghOfflineRunners = githubRunners.runners.Where(x => x.name.StartsWith("ghr") && x.status == "offline").ToList();
            List<GitHubRunner> ghIdleRunners = githubRunners.runners.Where(x => x.name.StartsWith("ghr") && x is { status: "online", busy: false }).ToList();

            foreach (var runnerToRemove in ghOfflineRunners)
            {
                _logger.LogInformation($"Removing offline runner {runnerToRemove.name} from org {org.OrgName}");
                await GitHubApi.RemoveRunner(org.OrgName, org.GitHubToken, runnerToRemove.id);
            }
           
            // Check how many base runners should be around and idle
            foreach (var size in Program.Config.Sizes)
            {
                List<GitHubRunner> idlePoolRunner = ghIdleRunners.Where(x => x.labels.Any(y => y.name == size.Name)).ToList();
                if (org.Pools.All(x => x.Size != size.Name))
                {
                    // Non of this size exists in pool. Remove all 
                    foreach (var r in idlePoolRunner)
                    { 
                        var htzSrv = allHtzSrvs.FirstOrDefault(x => x.Name == r.name);
                        if (htzSrv != null && DateTime.Now - htzSrv.Created < TimeSpan.FromMinutes(15))
                        {
                            // Don't cull a recently created runner. there might be a job waiting for pickup
                            continue;
                        }
                        
                        _logger.LogInformation($"Removing excess runner {r.name} from org {org.OrgName}");
                        await GitHubApi.RemoveRunner(org.OrgName, org.GitHubToken, r.id);
                        
                        long? htzSrvId = htzSrv?.Id;
                        if (htzSrvId.HasValue)
                        {
                            await _cc.DeleteRunner(htzSrvId.Value);
                        }
                    }
                }
               
            }
            foreach (var pool in org.Pools)
            {
                // get all idle runners of size
                List<GitHubRunner> idlePoolRunner = ghIdleRunners.Where(x => x.labels.Any(y => y.name == pool.Size)).ToList();
                while (idlePoolRunner.Count > pool.NumRunners)
                {
                    var r = idlePoolRunner.PopAt(0);
                    // Remove excess runners
                    _logger.LogInformation($"Removing excess runner {r.name} from org {org.OrgName}");
                    await GitHubApi.RemoveRunner(org.OrgName, org.GitHubToken, r.id);
                    long? htzSrvId = allHtzSrvs.FirstOrDefault(x => x.Name == r.name)?.Id;
                    if (htzSrvId.HasValue)
                    {
                        await _cc.DeleteRunner(htzSrvId.Value);
                    }
                }
            }
            
            // Get remaining runners registered to github
            githubRunners = await GitHubApi.GetRunners(org.GitHubToken, org.OrgName);
            registeredServerNames.AddRange(githubRunners.runners.Where(x => x.name.StartsWith("ghr")).Select(x => x.name));
        }
        
        // Remove every VM that's not in the github registered runners
        var remainingHtzServer = await _cc.GetAllServers();
        foreach (var htzSrv in remainingHtzServer)
        {
            if (registeredServerNames.Contains(htzSrv.Name))
            {
                // If we know the server in github, skip
                continue;
            }

            if (DateTime.UtcNow - htzSrv.Created.ToUniversalTime() < TimeSpan.FromMinutes(30))
            {
                // VM younger than 30min - not culling yet
                continue;
            }
            
            _logger.LogInformation($"Removing VM that is not in any GitHub registration: {htzSrv.Name} created at {htzSrv.Created:u}");
            await _cc.DeleteRunner(htzSrv.Id);
        }

    }

    private async Task<bool> DeleteRunner(DeleteRunnerTask rt)
    {
        try
        {
            await _cc.DeleteRunner(rt.ServerId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                $"Unable to delete runner [{rt.ServerId} | Retry: {rt.RetryCount}]: {ex.Message}");
            rt.RetryCount += 1;
            _queues.DeleteTasks.Enqueue(rt);
            return false;
        }
    }

    private async Task<bool> CreateRunner(CreateRunnerTask rt)
    {
        try
        {
            string newRunner = await _cc.CreateNewRunner(rt.Arch, rt.Size, rt.RunnerToken, rt.OrgName);
            _logger.LogInformation($"New Runner {newRunner} [{rt.Size} on {rt.Arch}] entering pool.");
            MachineCreatedCount.Labels(rt.OrgName, rt.Size).Inc();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Unable to create runner [{rt.Size} on {rt.Arch} | Retry: {rt.RetryCount}]: {ex.Message}");
            rt.RetryCount += 1;
            _queues.CreateTasks.Enqueue(rt);
            return false;
        }
    }
}