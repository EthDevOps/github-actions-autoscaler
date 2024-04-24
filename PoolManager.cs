using HetznerCloudApi.Object.Server;
using Prometheus;

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

        List<OrgConfiguration> orgConfig = Program.Config.OrgConfigs;
        
        // Cull runners
        List<Server> allHtzSrvs = await _cc.GetAllServers();

        await CleanUpRunners(orgConfig, allHtzSrvs);
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
                foreach (OrgConfiguration org in orgConfig)
                {
                    GithubRunnersGauge.Labels(org.OrgName, "active").Set(0);
                    GithubRunnersGauge.Labels(org.OrgName, "idle").Set(0);
                    GithubRunnersGauge.Labels(org.OrgName, "offline").Set(0);
                    GitHubRunners orgRunners = await GitHubApi.GetRunners(org.GitHubToken, org.OrgName);
                    var ghStatus = orgRunners.Runners.Where(x => x.Name.StartsWith("ghr")).GroupBy(x =>
                    {
                        if (x.Busy)
                        {
                            return "active";
                        }

                        if (x.Status == "online")
                        {
                            return "idle";
                        }

                        return x.Status;
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
                await CleanUpRunners(orgConfig, allHtzSrvs);
                await StartPoolRunners(orgConfig);
                crudeTimer = DateTime.UtcNow;
            }
           
            
            if (_queues.DeleteTasks.TryDequeue(out DeleteRunnerTask dtask))
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
            
            if (_queues.CreateTasks.TryDequeue(out CreateRunnerTask task))
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
        foreach (OrgConfiguration org in orgConfig)
        {
            _logger.LogInformation($"Checking pool runners for {org.OrgName}");
            string runnerToken = await GitHubApi.GetRunnerToken(org.GitHubToken, org.OrgName);
            List<Machine> existingRunners = _cc.GetRunnersForOrg(org.OrgName);
            
            foreach (Pool pool in org.Pools)
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

    private async Task CleanUpRunners(List<OrgConfiguration> orgConfig, List<Server> allHtzSrvs)
    {
        List<string> registeredServerNames = new();
        foreach (OrgConfiguration org in orgConfig)
        {
            _logger.LogInformation($"Culling runners for {org.OrgName}...");

            // Get runner infos
            GitHubRunners githubRunners = await GitHubApi.GetRunners(org.GitHubToken, org.OrgName);
           
            // Remove all offline runner entries from GitHub
            List<GitHubRunner> ghOfflineRunners = githubRunners.Runners.Where(x => x.Name.StartsWith("ghr") && x.Status == "offline").ToList();
            List<GitHubRunner> ghIdleRunners = githubRunners.Runners.Where(x => x.Name.StartsWith("ghr") && x is { Status: "online", Busy: false }).ToList();

            foreach (GitHubRunner runnerToRemove in ghOfflineRunners)
            {
                Server htzSrv = allHtzSrvs.FirstOrDefault(x => x.Name == runnerToRemove.Name);
                if (htzSrv != null && DateTime.UtcNow - htzSrv.Created.ToUniversalTime() < TimeSpan.FromMinutes(30))
                {
                    // VM younger than 30min - not culling yet
                    continue;
                }
                
                _logger.LogInformation($"Removing offline runner {runnerToRemove.Name} from org {org.OrgName}");
                await GitHubApi.RemoveRunner(org.OrgName, org.GitHubToken, runnerToRemove.Id);
            }
           
            // Check how many base runners should be around and idle
            foreach (MachineSize size in Program.Config.Sizes)
            {
                List<GitHubRunner> idlePoolRunner = ghIdleRunners.Where(x => x.Labels.Any(y => y.Name == size.Name)).ToList();
                if (org.Pools.All(x => x.Size != size.Name))
                {
                    // Non of this size exists in pool. Remove all 
                    foreach (GitHubRunner r in idlePoolRunner)
                    { 
                        Server htzSrv = allHtzSrvs.FirstOrDefault(x => x.Name == r.Name);
                        if (htzSrv != null && DateTime.Now - htzSrv.Created < TimeSpan.FromMinutes(15))
                        {
                            // Don't cull a recently created runner. there might be a job waiting for pickup
                            continue;
                        }
                        
                        _logger.LogInformation($"Removing excess runner {r.Name} from org {org.OrgName}");
                        await GitHubApi.RemoveRunner(org.OrgName, org.GitHubToken, r.Id);
                        
                        long? htzSrvId = htzSrv?.Id;
                        if (htzSrvId.HasValue)
                        {
                            await _cc.DeleteRunner(htzSrvId.Value);
                        }
                    }
                }
               
            }
            foreach (Pool pool in org.Pools)
            {
                // get all idle runners of size
                List<GitHubRunner> idlePoolRunner = ghIdleRunners.Where(x => x.Labels.Any(y => y.Name == pool.Size)).ToList();
                while (idlePoolRunner.Count > pool.NumRunners)
                {
                    GitHubRunner r = idlePoolRunner.PopAt(0);
                    // Remove excess runners
                    _logger.LogInformation($"Removing excess runner {r.Name} from org {org.OrgName}");
                    await GitHubApi.RemoveRunner(org.OrgName, org.GitHubToken, r.Id);
                    long? htzSrvId = allHtzSrvs.FirstOrDefault(x => x.Name == r.Name)?.Id;
                    if (htzSrvId.HasValue)
                    {
                        await _cc.DeleteRunner(htzSrvId.Value);
                    }
                }
            }
            
            // Get remaining runners registered to github
            githubRunners = await GitHubApi.GetRunners(org.GitHubToken, org.OrgName);
            registeredServerNames.AddRange(githubRunners.Runners.Where(x => x.Name.StartsWith("ghr")).Select(x => x.Name));
        }
        
        // Remove every VM that's not in the github registered runners
        List<Server> remainingHtzServer = await _cc.GetAllServers();
        foreach (Server htzSrv in remainingHtzServer)
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
            string newRunner = await _cc.CreateNewRunner(rt.Arch, rt.Size, rt.RunnerToken, rt.OrgName, rt.IsCustom, rt.ProfileName);
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