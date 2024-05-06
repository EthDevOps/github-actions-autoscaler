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

        List<GithubTargetConfiguration> targetConfig = Program.Config.TargetConfigs;
        
        // Cull runners
        List<Server> allHtzSrvs = await _cc.GetAllServers();

        await CleanUpRunners(targetConfig, allHtzSrvs);
        await StartPoolRunners(targetConfig);
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
                foreach (GithubTargetConfiguration tgt in targetConfig)
                {
                    GithubRunnersGauge.Labels(tgt.Name, "active").Set(0);
                    GithubRunnersGauge.Labels(tgt.Name, "idle").Set(0);
                    GithubRunnersGauge.Labels(tgt.Name, "offline").Set(0);
                    GitHubRunners orgRunners = tgt.Target switch
                    {
                        TargetType.Repository => await GitHubApi.GetRunnersForRepo(tgt.GitHubToken, tgt.Name),
                        TargetType.Organization => await GitHubApi.GetRunnersForOrg(tgt.GitHubToken, tgt.Name),
                        _ => throw new ArgumentOutOfRangeException()
                    };
                    
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
                        GithubRunnersGauge.Labels(tgt.Name, ghs.Status).Set(ghs.Count);
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
                _logger.LogInformation("Cleaning runners...");
                await CleanUpRunners(targetConfig, allHtzSrvs);
                await StartPoolRunners(targetConfig);
                crudeTimer = DateTime.UtcNow;
            }

            int deleteQueueSize = _queues.DeleteTasks.Count;
            // Run down the deletion queue in completion to potentially free up resources on HTZ cloud
            for (int i = 0; i < deleteQueueSize; i++)
            {
                if (!_queues.DeleteTasks.TryDequeue(out DeleteRunnerTask dtask)) continue;
                if (dtask != null)
                {
                    bool success = await DeleteRunner(dtask);
                    if (!success)
                    {
                        // Deletion didn't succeed. Let's hold processing runners for a minute
                        _logger.LogWarning(
                            "Encountered a problem deleting runners. Will hold queue processing for 10 seconds.");
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
            }

            if (_queues.CreateTasks.TryDequeue(out CreateRunnerTask task))
            {
                if (task != null)
                {
                    bool success = await CreateRunner(task);
                    if (!success)
                    {
                        // Creation didn't succeed. Let's hold of creating new runners for a minute
                        _logger.LogWarning("Encountered a problem creating runners. Will hold queue processing for 10 seconds.");
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    }
                }
            }

            await Task.Delay(250, stoppingToken);
        }
    }

    private async Task StartPoolRunners(List<GithubTargetConfiguration> targetConfig)
    {
        // Start pool runners
        foreach (GithubTargetConfiguration tgt in targetConfig)
        {
            _logger.LogInformation($"Checking pool runners for {tgt.Name}");
            string runnerToken = tgt.Target switch
            {
                TargetType.Repository => await GitHubApi.GetRunnerTokenForRepo(tgt.GitHubToken, tgt.Name),
                TargetType.Organization => await GitHubApi.GetRunnerTokenForOrg(tgt.GitHubToken, tgt.Name),
                _ => throw new ArgumentOutOfRangeException()
            };
                
            List<Machine> existingRunners = _cc.GetRunnersForTarget(tgt.Name);
            
            foreach (Pool pool in tgt.Pools)
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
                        OrgName = tgt.Name,
                        RepoName = tgt.Name,
                        TargetType = tgt.Target,
                        IsCustom = pool.Profile != "default",
                        ProfileName = pool.Profile
                        
                    }); 
                    _logger.LogInformation($"[{i+1}/{missingCt}] Queued {pool.Size} runner for {tgt.Name}");
                }
            }
        }
    }

    private async Task CleanUpRunners(List<GithubTargetConfiguration> targetConfigs, List<Server> allHtzSrvs)
    {
        List<string> registeredServerNames = new();
        foreach (GithubTargetConfiguration githubTarget in targetConfigs)
        {
            _logger.LogInformation($"Cleaning runners for {githubTarget.Name}...");

            // Get runner infos
            GitHubRunners githubRunners = githubTarget.Target switch
            {
                TargetType.Organization => await GitHubApi.GetRunnersForOrg(githubTarget.GitHubToken, githubTarget.Name),
                TargetType.Repository => await GitHubApi.GetRunnersForRepo(githubTarget.GitHubToken, githubTarget.Name),
                _ => throw new ArgumentOutOfRangeException()
            };

            // Remove all offline runner entries from GitHub
            List<GitHubRunner> ghOfflineRunners = githubRunners.Runners.Where(x => x.Name.StartsWith("ghr") && x.Status == "offline").ToList();
            List<GitHubRunner> ghIdleRunners = githubRunners.Runners.Where(x => x.Name.StartsWith("ghr") && x is { Status: "online", Busy: false }).ToList();

            foreach (GitHubRunner runnerToRemove in ghOfflineRunners)
            {
                Server htzSrv = allHtzSrvs.FirstOrDefault(x => x.Name == runnerToRemove.Name);
                if (htzSrv != null && DateTime.UtcNow - htzSrv.Created.ToUniversalTime() < TimeSpan.FromMinutes(30))
                {
                    // VM younger than 30min - not cleaning yet
                    continue;
                }
                
                _logger.LogInformation($"Removing offline runner {runnerToRemove.Name} from org {githubTarget.Name}");
                bool _ = githubTarget.Target switch
                {
                    TargetType.Organization => await GitHubApi.RemoveRunnerFromOrg(githubTarget.Name, githubTarget.GitHubToken, runnerToRemove.Id),
                    TargetType.Repository => await GitHubApi.RemoveRunnerFromRepo(githubTarget.Name, githubTarget.GitHubToken, runnerToRemove.Id),
                    _ => throw new ArgumentOutOfRangeException()
                };

            }
           
            // Check how many base runners should be around and idle
            foreach (MachineSize size in Program.Config.Sizes)
            {
                List<GitHubRunner> idlePoolRunner = ghIdleRunners.Where(x => x.Labels.Any(y => y.Name == size.Name)).ToList();
                if (githubTarget.Pools.All(x => x.Size != size.Name))
                {
                    // Non of this size exists in pool. Remove all 
                    foreach (GitHubRunner r in idlePoolRunner)
                    { 
                        Server htzSrv = allHtzSrvs.FirstOrDefault(x => x.Name == r.Name);
                        if (htzSrv != null && DateTime.Now - htzSrv.Created < TimeSpan.FromMinutes(15))
                        {
                            // Don't clean a recently created runner. there might be a job waiting for pickup
                            continue;
                        }
                        
                        _logger.LogInformation($"Removing excess runner {r.Name} from {githubTarget.Name}");
                        bool _ = githubTarget.Target switch
                        {
                            TargetType.Organization => await GitHubApi.RemoveRunnerFromOrg(githubTarget.Name, githubTarget.GitHubToken, r.Id),
                            TargetType.Repository => await GitHubApi.RemoveRunnerFromRepo(githubTarget.Name, githubTarget.GitHubToken, r.Id),
                            _ => throw new ArgumentOutOfRangeException()
                        };
                        
                        long? htzSrvId = htzSrv?.Id;
                        if (htzSrvId.HasValue)
                        {
                            await _cc.DeleteRunner(htzSrvId.Value);
                        }
                    }
                }
               
            }
            foreach (Pool pool in githubTarget.Pools)
            {
                // get all idle runners of size
                List<GitHubRunner> idlePoolRunner = ghIdleRunners.Where(x => x.Labels.Any(y => y.Name == pool.Size)).ToList();
                while (idlePoolRunner.Count > pool.NumRunners)
                {
                    GitHubRunner r = idlePoolRunner.PopAt(0);
                    // Remove excess runners
                    _logger.LogInformation($"Removing excess runner {r.Name} from {githubTarget.Name}");
                    bool _ = githubTarget.Target switch
                    {
                        TargetType.Organization => await GitHubApi.RemoveRunnerFromOrg(githubTarget.Name, githubTarget.GitHubToken, r.Id),
                        TargetType.Repository => await GitHubApi.RemoveRunnerFromRepo(githubTarget.Name, githubTarget.GitHubToken, r.Id),
                        _ => throw new ArgumentOutOfRangeException()
                    };
                    long? htzSrvId = allHtzSrvs.FirstOrDefault(x => x.Name == r.Name)?.Id;
                    if (htzSrvId.HasValue)
                    {
                        await _cc.DeleteRunner(htzSrvId.Value);
                    }
                }
            }
            
            // Get remaining runners registered to github
            githubRunners = githubTarget.Target switch
            {
                TargetType.Organization => await GitHubApi.GetRunnersForOrg(githubTarget.GitHubToken, githubTarget.Name),
                TargetType.Repository => await GitHubApi.GetRunnersForRepo(githubTarget.GitHubToken, githubTarget.Name),
                _ => throw new ArgumentOutOfRangeException()
            };
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
                // VM younger than 30min - not cleaning yet
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
            string targetName = rt.TargetType switch
            {
                TargetType.Repository => rt.RepoName,
                TargetType.Organization => rt.OrgName,
                _ => throw new ArgumentOutOfRangeException()
            };
            string newRunner = await _cc.CreateNewRunner(rt.Arch, rt.Size, rt.RunnerToken, targetName, rt.IsCustom, rt.ProfileName);
            _logger.LogInformation($"New Runner {newRunner} [{rt.Size} on {rt.Arch}] entering pool for {targetName}.");
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