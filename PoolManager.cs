using System.Collections.Concurrent;
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

    public ConcurrentQueue<RunnerTask?> Tasks { get; }

    public PoolManager(CloudController cc, ILogger<PoolManager> logger)
    {
        _cc = cc;
        _logger = logger;
        Tasks = new ConcurrentQueue<RunnerTask?>();
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do init shit
        _logger.LogInformation("PoolManager online.");
        _logger.LogInformation("Queuing base load runner start...");

        var orgConfig = Program.Config.OrgConfigs;
        
        // Cull runners
        List<Server> allHtzSrvs = await _cc.GetAllServers();

        foreach (OrgConfiguration org in orgConfig)
        {
           _logger.LogInformation($"Culling runners for {org.OrgName}...");

           // Get runner infos
           GitHubRunners githubRunners = await GitHubApi.GetRunners(org.GitHubToken, org.OrgName);
           
           // Remove all offline runner entries from GitHub
           List<GitHubRunner> ghOfflineRunners = githubRunners.runners.Where(x => x.status == "offline").ToList();
           List<GitHubRunner> ghIdleRunners = githubRunners.runners.Where(x => x is { status: "online", busy: false }).ToList();

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
                      _logger.LogInformation($"Removing excess runner {r.name} from org {org.OrgName}");
                       await GitHubApi.RemoveRunner(org.OrgName, org.GitHubToken, r.id);
                       long? htzSrvId = allHtzSrvs.FirstOrDefault(x => x.Name == r.name)?.Id;
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

        }
        
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
                    Tasks.Enqueue(new RunnerTask
                    {
                        Action = RunnerAction.Create,
                        Arch = arch,
                        Size = pool.Size,
                        RunnerToken = runnerToken,
                        OrgName = org.OrgName
                    }); 
                    _logger.LogInformation($"[{i+1}/{missingCt}] Queued {pool.Size} runner for {org.OrgName}");
                }
            }
        }
        _logger.LogInformation("Poolmanager init done.");

        // Kick the PoolManager into background
        await Task.Yield();
        
        while (!stoppingToken.IsCancellationRequested)
        {
            QueueSize.Set(Tasks.Count);
            if (Tasks.TryDequeue(out RunnerTask? task))
            {
                _logger.LogInformation($"Current Queue length: {Tasks.Count}");
                if (task != null)
                {
                    bool success = false;
                    switch (task.Action)
                    {
                        case RunnerAction.Create:
                            success = await CreateRunner(task);
                            break;
                        case RunnerAction.Delete:
                            success = await DeleteRunner(task);
                            break;
                    }
                    if (!success)
                    {
                        // Creation didn't succeed. Let's hold of creating new runners for a minute
                        _logger.LogWarning("Encountered a problem creating runners. Will hold creation for 1 minute.");
                        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    }
                }
            }

            await Task.Delay(1000, stoppingToken);
        }
    }

    private async Task<bool> DeleteRunner(RunnerTask rt)
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
            Tasks.Enqueue(rt);
            return false;
        }
    }

    private async Task<bool> CreateRunner(RunnerTask rt)
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
            Tasks.Enqueue(rt);
            return false;
        }
    }
}

public static class ListExtensionMethods
{
    public static T PopAt<T>(this List<T> list, int index)
    {
        var r = list[index];
        list.RemoveAt(index);
        return r;
    }

    public static T PopFirst<T>(this List<T> list, Predicate<T> predicate)
    {
        var index = list.FindIndex(predicate);
        var r = list[index];
        list.RemoveAt(index);
        return r;
    }

    public static T PopFirstOrDefault<T>(this List<T> list, Predicate<T> predicate) where T : class
    {
        var index = list.FindIndex(predicate);
        if (index > -1)
        {
            var r = list[index];
            list.RemoveAt(index);
            return r;
        }
        return null;
    }
}