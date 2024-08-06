using GithubActionsOrchestrator.Database;
using GithubActionsOrchestrator.GitHub;
using GithubActionsOrchestrator.Models;
using HetznerCloudApi.Object.Server;
using Microsoft.EntityFrameworkCore;
using Prometheus;

namespace GithubActionsOrchestrator;

public class PoolManager : BackgroundService
{
    private readonly CloudController _cc;
    private readonly ILogger<PoolManager> _logger;
    private static readonly Counter MachineCreatedCount = Metrics
        .CreateCounter("github_autoscaler_machines_created", "Number of created machines", labelNames: ["org","size"]);
    private static readonly Gauge CreateQueueSize = Metrics
        .CreateGauge("github_autoscaler_create_queue", "Number of queued runner create tasks");
    private static readonly Gauge GithubRunnersGauge = Metrics
        .CreateGauge("github_registered_runners", "Number of runners registered to github actions", labelNames: ["org", "status"]);
    private static readonly Gauge DeleteQueueSize = Metrics
        .CreateGauge("github_autoscaler_delete_queue", "Number of queued runner delete tasks");
    private static readonly Gauge ProvisionQueueSize = Metrics
        .CreateGauge("github_autoscaler_runners_provisioning", "Number of runners currently provisioning");
    private static readonly Gauge CspRunnerCount = Metrics
        .CreateGauge("github_autoscaler_csp_runners", "Number of runners currently on the CSP", labelNames: ["csp"]);
    private static readonly Gauge StuckJobsCount = Metrics
        .CreateGauge("github_autoscaler_job_stuck", "Number of jobs not picked up after 15min");
    private static readonly Gauge QueuedJobsCount = Metrics
        .CreateGauge("github_autoscaler_job_queued", "Total Number of jobs queued");
    private static readonly Gauge CompletedJobsCount = Metrics
        .CreateGauge("github_autoscaler_job_completed", "Total Number of jobs completed");
    private static readonly Gauge InProgressJobsCount = Metrics
        .CreateGauge("github_autoscaler_job_inprogress", "Total Number of jobs inprogress");

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
        List<Server> allHtzSrvs = await _cc.GetAllServersFromCsp();

        await CleanUpRunners(targetConfig);
        await StartPoolRunners(targetConfig);
        _logger.LogInformation("Poolmanager init done.");

        // Kick the PoolManager into background
        await Task.Yield();
       
        DateTime crudeTimer = DateTime.UtcNow;
        DateTime crudeStatsTimer = DateTime.UtcNow;
        int cullMinutes = 5;
        int statsSeconds = 10;
        
        while (!stoppingToken.IsCancellationRequested)
        {
            

            if (DateTime.UtcNow - crudeStatsTimer > TimeSpan.FromSeconds(statsSeconds))
            {
                // Grab some stats
                await ProcessStats(targetConfig);
                crudeStatsTimer = DateTime.UtcNow;
            }


            // check for culling interval
            if (DateTime.UtcNow - crudeTimer > TimeSpan.FromMinutes(cullMinutes))
            {
                _logger.LogInformation("Cleaning runners...");
                await CleanUpRunners(targetConfig);
                await StartPoolRunners(targetConfig);
                await CheckForStuckJobs(targetConfig);
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
                        _logger.LogWarning($"Encountered a problem creating runner for {task.RepoName}. Will hold queue processing for 10 seconds.");
                        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                    }
                }
            }

            await Task.Delay(250, stoppingToken);
        }
    }

    private async Task ProcessStats(List<GithubTargetConfiguration> targetConfig)
    {
        CreateQueueSize.Set(_queues.CreateTasks.Count);
        DeleteQueueSize.Set(_queues.DeleteTasks.Count);
        ProvisionQueueSize.Set(_queues.CreatedRunners.Count);
        CspRunnerCount.Labels("htz").Set(await _cc.GetServerCountFromCsp()); 
        
        // Grab job state counts
        var db = new ActionsRunnerContext();
        var stuckTime = DateTime.UtcNow - TimeSpan.FromMinutes(15);
        var stuckJobs = await db.Jobs.CountAsync(x => x.State == JobState.Queued && x.RunnerId == null && x.QueueTime < stuckTime);
        StuckJobsCount.Set(stuckJobs);

        var jobsByState = await db.Jobs.GroupBy(x => x.State).Select(x => new { x.Key, Count = x.Count() }).ToListAsync();
       
        QueuedJobsCount.Set(jobsByState.FirstOrDefault(x => x.Key == JobState.Queued)?.Count ?? 0);
        CompletedJobsCount.Set(jobsByState.FirstOrDefault(x => x.Key == JobState.Completed)?.Count ?? 0);
        InProgressJobsCount.Set(jobsByState.FirstOrDefault(x => x.Key == JobState.InProgress)?.Count ?? 0);
        
        // grab runner state counts
        
        // Github runner stats
        try
        {
            foreach (GithubTargetConfiguration tgt in targetConfig)
            {
                GithubRunnersGauge.Labels(tgt.Name, "active").Set(0);
                GithubRunnersGauge.Labels(tgt.Name, "idle").Set(0);
                GithubRunnersGauge.Labels(tgt.Name, "offline").Set(0);
                List<GitHubRunner> orgRunners = tgt.Target switch
                {
                    TargetType.Repository => await GitHubApi.GetRunnersForRepo(tgt.GitHubToken, tgt.Name),
                    TargetType.Organization => await GitHubApi.GetRunnersForOrg(tgt.GitHubToken, tgt.Name),
                    _ => throw new ArgumentOutOfRangeException()
                };

                var ghStatus = orgRunners.Where(x => x.Name.StartsWith(Program.Config.RunnerPrefix)).GroupBy(x =>
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
    }

    private async Task StartPoolRunners(List<GithubTargetConfiguration> targetConfig)
    {
        // Start pool runners
        var db = new ActionsRunnerContext();
        foreach (GithubTargetConfiguration owner in targetConfig)
        {
            _logger.LogInformation($"Checking pool runners for {owner.Name}");
            string runnerToken = owner.Target switch
            {
                TargetType.Repository => await GitHubApi.GetRunnerTokenForRepo(owner.GitHubToken, owner.Name),
                TargetType.Organization => await GitHubApi.GetRunnerTokenForOrg(owner.GitHubToken, owner.Name),
                _ => throw new ArgumentOutOfRangeException()
            };
                
            List<Runner> existingRunners = await db.Runners.Where(x => x.Owner == owner.Name && x.IsOnline).ToListAsync();
            
            foreach (Pool pool in owner.Pools)
            {
                int existCt = existingRunners.Count(x => x.Size == pool.Size);
                int missingCt = pool.NumRunners - existCt;

                string arch = Program.Config.Sizes.FirstOrDefault(x => x.Name == pool.Size)?.Arch;
                
                _logger.LogInformation($"Checking pool {pool.Size} [{arch}]: Existing={existCt} Requested={pool.NumRunners} Missing={missingCt}");
                
                for (int i = 0; i < missingCt; i++)
                {
                    // Queue VM creation
                    var profile = pool.Profile ?? "default";
                    Runner newRunner = new()
                    {
                        Size = pool.Size,
                        Cloud = "htz",
                        Hostname = "Unknown",
                        Profile = profile,
                        Lifecycle =
                        [
                            new RunnerLifecycle
                            {
                                EventTimeUtc = DateTime.UtcNow,
                                Status = RunnerStatus.CreationQueued,
                                Event = "Created as pool runner"
                            }
                        ],
                        IsOnline = false,
                        Arch = arch,
                        IPv4 = string.Empty,
                        IsCustom = profile != "default",
                        Owner = owner.Name
                    };
                    await db.Runners.AddAsync(newRunner);
                    await db.SaveChangesAsync();
                    
                    _queues.CreateTasks.Enqueue(new CreateRunnerTask
                    {
                        RunnerToken = runnerToken,
                        RepoName = owner.Name,
                        TargetType = owner.Target,
                        RunnerDbId = newRunner.RunnerId,
                        
                    }); 
                    _logger.LogInformation($"[{i+1}/{missingCt}] Queued {pool.Size} runner for {owner.Name}");
                }
            }
        }
    }

    private async Task CheckForStuckJobs(List<GithubTargetConfiguration> targetConfig)
    {
        var db = new ActionsRunnerContext();
        var stuckTime = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        var stuckJobs = await db.Jobs.Where(x => x.State == JobState.Queued && x.RunnerId == null && x.QueueTime < stuckTime).ToListAsync();
        foreach (var stuckJob in stuckJobs)
        {
            _logger.LogWarning($"Found stuck Job: {stuckJob.JobId} in {stuckJob.Repository}. Starting new runner to compensate...");

            var owner = targetConfig.FirstOrDefault(x => x.Name == stuckJob.Owner);
            if (owner == null)
            {
                _logger.LogError($"Unable to get owner for stuck job. {stuckJob.JobId}");
                continue;
            }
            
            string runnerToken = owner.Target switch
            {
                TargetType.Repository => await GitHubApi.GetRunnerTokenForRepo(owner.GitHubToken, owner.Name),
                TargetType.Organization => await GitHubApi.GetRunnerTokenForOrg(owner.GitHubToken, owner.Name),
                _ => throw new ArgumentOutOfRangeException()
            };
            var profile = stuckJob.RequestedProfile ?? "default";
            string arch = Program.Config.Sizes.FirstOrDefault(x => x.Name == stuckJob.RequestedSize)?.Arch;
            Runner newRunner = new()
            {
                Size = stuckJob.RequestedSize,
                Cloud = "htz",
                Hostname = "Unknown",
                Profile = profile,
                Lifecycle =
                [
                    new RunnerLifecycle
                    {
                        EventTimeUtc = DateTime.UtcNow,
                        Status = RunnerStatus.CreationQueued,
                        Event = $"Created for stuck job {stuckJob.JobId}"
                    }
                ],
                IsOnline = false,
                Arch = arch,
                IPv4 = string.Empty,
                IsCustom = profile != "default",
                Owner = stuckJob.Owner
            };
            await db.Runners.AddAsync(newRunner);
            await db.SaveChangesAsync();
            
            _queues.CreateTasks.Enqueue(new CreateRunnerTask
            {
                RunnerToken = runnerToken,
                RepoName = stuckJob.Repository,
                TargetType = owner.Target,
                RunnerDbId = newRunner.RunnerId,
                
            }); 
        } 
    }

    private async Task CleanUpRunners(List<GithubTargetConfiguration> targetConfigs)
    {
        List<string> registeredServerNames = new();
        var db = new ActionsRunnerContext();
        foreach (GithubTargetConfiguration githubTarget in targetConfigs)
        {
            _logger.LogInformation($"Cleaning runners for {githubTarget.Name}...");

            // Get runner infos
            List<GitHubRunner> githubRunners = githubTarget.Target switch
            {
                TargetType.Organization => await GitHubApi.GetRunnersForOrg(githubTarget.GitHubToken, githubTarget.Name),
                TargetType.Repository => await GitHubApi.GetRunnersForRepo(githubTarget.GitHubToken, githubTarget.Name),
                _ => throw new ArgumentOutOfRangeException()
            };

            // Remove all offline runner entries from GitHub
            List<GitHubRunner> ghOfflineRunners = githubRunners.Where(x => x.Name.StartsWith(Program.Config.RunnerPrefix) && x.Status == "offline").ToList();
            foreach (GitHubRunner runnerToRemove in ghOfflineRunners)
            {
                var runner = await db.Runners.Include(x => x.Lifecycle).FirstOrDefaultAsync(x => x.Hostname == runnerToRemove.Name);
                if (runner == null)
                {
                    _logger.LogWarning($"Found offline runner on GitHub not on record: {runnerToRemove.Name} - Removing");
                    bool f = githubTarget.Target switch
                    {
                        TargetType.Organization => await GitHubApi.RemoveRunnerFromOrg(githubTarget.Name, githubTarget.GitHubToken, runnerToRemove.Id),
                        TargetType.Repository => await GitHubApi.RemoveRunnerFromRepo(githubTarget.Name, githubTarget.GitHubToken, runnerToRemove.Id),
                        _ => throw new ArgumentOutOfRangeException()
                    };
                    continue;
                }

                if (DateTime.UtcNow - runner.CreateTime < TimeSpan.FromMinutes(30))
                {
                    // VM younger than 30min - not cleaning yet
                    continue;
                }

                runner.IsOnline = false;
                runner.Lifecycle.Add(new()
                {
                    Status = RunnerStatus.Cleanup,
                    EventTimeUtc = DateTime.UtcNow,
                    Event = $"[GitHub] Removing offline runner {runnerToRemove.Name} from org {githubTarget.Name}"
                });
                await db.SaveChangesAsync();
                _logger.LogInformation($"Removing offline runner {runnerToRemove.Name} from org {githubTarget.Name}");
                bool _ = githubTarget.Target switch
                {
                    TargetType.Organization => await GitHubApi.RemoveRunnerFromOrg(githubTarget.Name, githubTarget.GitHubToken, runnerToRemove.Id),
                    TargetType.Repository => await GitHubApi.RemoveRunnerFromRepo(githubTarget.Name, githubTarget.GitHubToken, runnerToRemove.Id),
                    _ => throw new ArgumentOutOfRangeException()
                };

            }
           
            // remove any long idling runners. pool manager will start fresh ones eventually if needed. Keeps em fresh.
            List<GitHubRunner> ghIdleRunners = githubRunners.Where(x => x.Name.StartsWith(Program.Config.RunnerPrefix) && x is { Status: "online", Busy: false }).ToList();
            foreach (GitHubRunner ghIdleRunner in ghIdleRunners)
            {
                var runner = await db.Runners.Include(x => x.Lifecycle).FirstOrDefaultAsync(x => x.Hostname == ghIdleRunner.Name);
                if (runner == null)
                {
                    _logger.LogWarning($"Found idle runner on GitHub not on record: {ghIdleRunner.Name}");
                    continue;
                }

                if (DateTime.UtcNow - runner.CreateTime < TimeSpan.FromHours(6))
                {
                    // VM younger than 6h - not cleaning yet
                    continue;
                }
                
                // Delete before writing to db.
                bool _ = githubTarget.Target switch
                {
                    TargetType.Organization => await GitHubApi.RemoveRunnerFromOrg(githubTarget.Name, githubTarget.GitHubToken, ghIdleRunner.Id),
                    TargetType.Repository => await GitHubApi.RemoveRunnerFromRepo(githubTarget.Name, githubTarget.GitHubToken, ghIdleRunner.Id),
                    _ => throw new ArgumentOutOfRangeException()
                };
               
                runner.IsOnline = false;
                runner.Lifecycle.Add(new()
                {
                    Status = RunnerStatus.DeletionQueued,
                    EventTimeUtc = DateTime.UtcNow,
                    Event = $"Removing excessive idle runner {ghIdleRunner.Name} from org {githubTarget.Name}"
                });
                await db.SaveChangesAsync();
                _logger.LogInformation($"Removing idle runner {ghIdleRunner.Name} from org {githubTarget.Name}");
                _queues.DeleteTasks.Enqueue(new()
                {
                    ServerId = runner.CloudServerId,
                    RunnerDbId = runner.RunnerId
                }); 
            }
            
            // Get remaining runners registered to github
            githubRunners = githubTarget.Target switch
            {
                TargetType.Organization => await GitHubApi.GetRunnersForOrg(githubTarget.GitHubToken, githubTarget.Name),
                TargetType.Repository => await GitHubApi.GetRunnersForRepo(githubTarget.GitHubToken, githubTarget.Name),
                _ => throw new ArgumentOutOfRangeException()
            };
            registeredServerNames.AddRange(githubRunners.Where(x => x.Name.StartsWith(Program.Config.RunnerPrefix)).Select(x => x.Name));
        }
        
        // Remove every VM that's not in the github registered runners
        List<Server> remainingHtzServer = await _cc.GetAllServersFromCsp();
        foreach (Server htzSrv in remainingHtzServer)
        {
            if (registeredServerNames.Contains(htzSrv.Name))
            {
                // If we know the server in github, skip
                continue;
            }

            var runner = await db.Runners.Include(x => x.Lifecycle).FirstOrDefaultAsync(x => x.CloudServerId == htzSrv.Id);
            if (runner.Lifecycle.Any(x => x.Status == RunnerStatus.DeletionQueued))
            {
                runner.Lifecycle.Add(new()
                {
                    Status = RunnerStatus.DeletionQueued,
                    Event = "Don't queue deletion due to Github registration. Runner already queued for deletion.",
                    EventTimeUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
                
            }
            else if ((runner.LastState >= RunnerStatus.Provisioned && DateTime.UtcNow - runner.LastStateTime > TimeSpan.FromMinutes(5)) || 
                     (DateTime.UtcNow - runner.LastStateTime > TimeSpan.FromMinutes(30)))
            {
                _logger.LogInformation($"Removing VM that is not in any GitHub registration: {htzSrv.Name} created at {htzSrv.Created:u}");
                runner.IsOnline = false;
                runner.Lifecycle.Add(new()
                {
                    Status = RunnerStatus.DeletionQueued,
                    Event = "Removing as VM not longer in any GitHub registration",
                    EventTimeUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
                _queues.DeleteTasks.Enqueue(new()
                {
                    RunnerDbId = runner.RunnerId,
                    ServerId = htzSrv.Id
                });
                    
            }
            
        }
        
    }

    private async Task<bool> DeleteRunner(DeleteRunnerTask rt)
    {
        var db = new ActionsRunnerContext();
        var runner = await db.Runners.Include(x => x.Lifecycle).FirstOrDefaultAsync(x => x.RunnerId == rt.RunnerDbId);
        
        try
        {
            if (runner.Lifecycle.Any(x => x.Status == RunnerStatus.DeletionQueued))
            {
                runner.IsOnline = false;
                runner.Lifecycle.Add(new()
                {
                    Status = RunnerStatus.Deleted,
                    EventTimeUtc = DateTime.UtcNow,
                    Event = "Runner already queued for deletion."
                });
            }
            else
            {

                await _cc.DeleteRunner(rt.ServerId);
                runner.IsOnline = false;
                runner.Lifecycle.Add(new()
                {
                    Status = RunnerStatus.Deleted,
                    EventTimeUtc = DateTime.UtcNow,
                    Event = "Runner was successfully deleted from CSP"
                });
            }
            await db.SaveChangesAsync();

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                $"Unable to delete runner [{rt.ServerId} | Retry: {rt.RetryCount}]: {ex.Message}");
            rt.RetryCount += 1;
            if (rt.RetryCount < 3)
            {
                _queues.DeleteTasks.Enqueue(rt);
                runner.Lifecycle.Add(new RunnerLifecycle
                {
                    Status = RunnerStatus.Failure,
                    EventTimeUtc = DateTime.UtcNow,
                    Event = $"Unable to delete runner | Retry: {rt.RetryCount}: {ex.Message}"
                });
            }
            else
            {
                _logger.LogError($"Retries exceeded for {rt.ServerId}. Giving up.");
                runner.Lifecycle.Add(new RunnerLifecycle
                {
                    Status = RunnerStatus.Failure,
                    EventTimeUtc = DateTime.UtcNow,
                    Event = $"Retries exceeded deleting runner. Giving up. | Retry: {rt.RetryCount}: {ex.Message}"
                });
            }

            await db.SaveChangesAsync();
            return false;
        }
    }

    private async Task<bool> CreateRunner(CreateRunnerTask rt)
    {
        var db = new ActionsRunnerContext();
        var runner = await db.Runners.Include(x => x.Lifecycle).FirstOrDefaultAsync(x => x.RunnerId == rt.RunnerDbId);
        try
        {
            string targetName = rt.TargetType switch
            {
                TargetType.Repository => rt.RepoName,
                TargetType.Organization => runner.Owner,
                _ => throw new ArgumentOutOfRangeException()
            };
            Machine newRunner = await _cc.CreateNewRunner(runner.Arch, runner.Size, rt.RunnerToken, targetName, runner.IsCustom, runner.Profile);
            _logger.LogInformation($"New Runner {newRunner.Name} [{runner.Size} on {runner.Arch}] entering pool for {targetName}.");
            MachineCreatedCount.Labels(runner.Owner, runner.Size).Inc();

            runner.Hostname = newRunner.Name;
            runner.IsOnline = true;
            runner.CloudServerId = newRunner.Id;
            runner.IPv4 = newRunner.Ipv4;
            
            runner.Lifecycle.Add(new RunnerLifecycle
            {
                Status = RunnerStatus.Created,
                EventTimeUtc = DateTime.UtcNow,
                Event = $"New Runner {newRunner.Name} [{runner.Size} on {runner.Arch}] entering pool for {targetName}."
            });
            await db.SaveChangesAsync();
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Unable to create runner [{runner.Size} on {runner.Arch} | Retry: {rt.RetryCount}]: {ex.Message}");
            runner.Lifecycle.Add(new RunnerLifecycle
            {
                Status = RunnerStatus.Failure,
                EventTimeUtc = DateTime.UtcNow,
                Event = $"Unable to create runner [{runner.Size} on {runner.Arch} | Retry: {rt.RetryCount}]: {ex.Message}"
            });
            rt.RetryCount += 1;
            if (rt.RetryCount < 3)
            {
                _queues.CreateTasks.Enqueue(rt);
            }
            else
            {
                _logger.LogError($"Retries exceeded for {runner.Size} on {runner.Arch}. giving up.");
                runner.Lifecycle.Add(new RunnerLifecycle
                {
                    Status = RunnerStatus.Failure,
                    EventTimeUtc = DateTime.UtcNow,
                    Event = $"Retries exceeded for {runner.Size} on {runner.Arch}. giving up."
                });
            }
            await db.SaveChangesAsync();
            return false;
        }
    }
}