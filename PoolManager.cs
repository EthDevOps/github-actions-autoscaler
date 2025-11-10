using System.Reflection.Metadata.Ecma335;
using GithubActionsOrchestrator.CloudControllers;
using GithubActionsOrchestrator.Database;
using GithubActionsOrchestrator.GitHub;
using GithubActionsOrchestrator.Models;
using HetznerCloudApi.Object.Server;
using Microsoft.EntityFrameworkCore;
using Prometheus;

namespace GithubActionsOrchestrator;

public class PoolManager : BackgroundService
{
    private readonly List<ICloudController> _cc;
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
    private static readonly Gauge ThrottledJobsCount = Metrics
        .CreateGauge("github_autoscaler_job_throttled", "Number of jobs waiting due to runner quota limit");
    private static readonly Gauge QueuedJobsCount = Metrics
        .CreateGauge("github_autoscaler_job_queued", "Total Number of jobs queued");
    private static readonly Gauge CompletedJobsCount = Metrics
        .CreateGauge("github_autoscaler_job_completed", "Total Number of jobs completed");
    private static readonly Gauge InProgressJobsCount = Metrics
        .CreateGauge("github_autoscaler_job_inprogress", "Total Number of jobs inprogress");

    private readonly RunnerQueue _queues;

    private List<CloudBan> _bannedClouds = new List<CloudBan>();

    public PoolManager(IEnumerable<ICloudController> cc, ILogger<PoolManager> logger, RunnerQueue queues)
    {
        List<ICloudController> cloudControllers = cc.ToList();
        _cc = cloudControllers;
        _logger = logger;
        _queues = queues;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do init shit
        _logger.LogInformation("PoolManager online.");
        _logger.LogInformation("Queuing base load runner start...");

        List<GithubTargetConfiguration> targetConfig = Program.Config.TargetConfigs;
        
        await CleanUpRunners(targetConfig);
        await StartPoolRunners(targetConfig);
        _logger.LogInformation("Poolmanager init done.");

        // Kick the PoolManager into background
        await Task.Yield();
       
        DateTime crudeTimer = DateTime.UtcNow;
        DateTime crudeStatsTimer = DateTime.UtcNow;
        int cullMinutes = 1;
        int statsSeconds = 10;
        
        while (!stoppingToken.IsCancellationRequested)
        {
            

            if (DateTime.UtcNow - crudeStatsTimer > TimeSpan.FromSeconds(statsSeconds))
            {
                // Update config
                Program.LoadConfiguration();
                targetConfig = Program.Config.TargetConfigs;
                
                // Grab some stats
                await ProcessStats(targetConfig);
                crudeStatsTimer = DateTime.UtcNow;
            }


            // check for culling interval
            if (DateTime.UtcNow - crudeTimer > TimeSpan.FromMinutes(cullMinutes))
            {
                _logger.LogInformation("Cleaning runners...");

                var checkInId = SentrySdk.CaptureCheckIn("CheckForStuckRunners", CheckInStatus.InProgress);
                try
                {
                    await CheckForStuckRunners(targetConfig);
                    SentrySdk.CaptureCheckIn("CheckForStuckRunners", CheckInStatus.Ok, checkInId);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Unable to check for stuck runners: {ex.GetFullExceptionDetails()}");
                    SentrySdk.CaptureCheckIn("CheckForStuckRunners", CheckInStatus.Error, checkInId);
                    SentrySdk.CaptureException(ex);
                }

                await CleanUpRunners(targetConfig);
                
                await StartPoolRunners(targetConfig);
                await CheckForStuckJobs(targetConfig);

                await CleanupDatabase();

                foreach (var ban in _bannedClouds.ToList())
                {
                    if (ban.UnbanTime < DateTime.UtcNow)
                    {
                        _logger.LogInformation($"Unbanned {ban.Size} on {ban.Cloud}...");
                        _bannedClouds.Remove(ban);
                    }
                }
                
                crudeTimer = DateTime.UtcNow;
            }

            int deleteQueueSize = _queues.DeleteTasks.Count;
            var deletionTasks = new List<Task>();
            // Run down the deletion queue in completion to potentially free up resources on HTZ cloud
            for (int i = 0; i < deleteQueueSize; i++)
            {
                if (!_queues.DeleteTasks.TryDequeue(out DeleteRunnerTask dtask)) continue;
                if (dtask != null)
                {
                    var deletionTask = DeleteRunner(dtask);
                    deletionTasks.Add(deletionTask);
                    await Task.Delay(250, stoppingToken);
                }
            }

            Task.WaitAll(deletionTasks, stoppingToken);
            
            // Process creation tasks in parallel with delay between starts
            var runningTasks = new List<Task>();
            var lastTaskStartTime = DateTime.UtcNow;

            while (runningTasks.Count < Program.Config.ParallelOperations && _queues.CreateTasks.TryDequeue(out CreateRunnerTask task))
                
            {
                if (task == null) continue;

                // Ensure 500ms spacing between task starts
                var timeSinceLastStart = DateTime.UtcNow - lastTaskStartTime;
                if (timeSinceLastStart < TimeSpan.FromMilliseconds(500))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500) - timeSinceLastStart, stoppingToken);
                }

                // Start new task and add to running tasks
                runningTasks.Add(Task.Run(async () =>
                {
                    bool success = await CreateRunner(task);
                    if (!success)
                    {
                        _logger.LogWarning($"Encountered a problem creating runner for {task.RepoName}.");
                    }
                }, stoppingToken));

                lastTaskStartTime = DateTime.UtcNow;
            }

            // Wait for all running tasks to complete
            if (runningTasks.Any())
            {
                try
                {
                    await Task.WhenAll(runningTasks);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error while processing creation tasks: {ex.Message}");
                }
            }

            
            /*if (_queues.CreateTasks.TryDequeue(out CreateRunnerTask task))
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
            }*/

            await Task.Delay(250, stoppingToken);
        }
    }

    private async Task CleanupDatabase()
    {
        await using var db = new ActionsRunnerContext();

        var cutoffTime = DateTime.UtcNow - TimeSpan.FromDays(30);

        // Remove old runners - find runners where their earliest lifecycle event is older than 30 days
        var oldRunnerIds = await db.RunnerLifecycles
            .GroupBy(rl => rl.RunnerId)
            .Select(g => new { RunnerId = g.Key, LatestEvent = g.Max(rl => rl.EventTimeUtc) })
            .Where(r => r.LatestEvent < cutoffTime)
            .Select(r => r.RunnerId)
            .ToListAsync();

        if (oldRunnerIds.Count > 0)
        {
            _logger.LogInformation($"Removing {oldRunnerIds.Count} runners older than 30 days from database");

            // Delete RunnerLifecycles first (they reference runners)
            var oldLifecycles = await db.RunnerLifecycles.Where(rl => oldRunnerIds.Contains(rl.RunnerId)).ToListAsync();
            db.RunnerLifecycles.RemoveRange(oldLifecycles);
            await db.SaveChangesAsync();

            // Break circular dependencies by nulling out foreign keys
            var oldRunners = await db.Runners.Where(r => oldRunnerIds.Contains(r.RunnerId)).ToListAsync();
            var oldJobs = await db.Jobs.Where(j => oldRunnerIds.Contains(j.RunnerId.Value)).ToListAsync();

            // Null out the foreign keys to break circular reference
            foreach (var runner in oldRunners)
            {
                runner.JobId = null;
            }
            foreach (var job in oldJobs)
            {
                job.RunnerId = null;
            }
            await db.SaveChangesAsync();

            // Now delete the runners and jobs
            db.Runners.RemoveRange(oldRunners);
            db.Jobs.RemoveRange(oldJobs);
            await db.SaveChangesAsync();
        }

        // Remove old completed jobs - only remove completed jobs older than 30 days
        var oldCompletedJobs = await db.Jobs
            .Where(j => j.CompleteTime < cutoffTime && j.CompleteTime != DateTime.MinValue && j.RunnerId == null)
            .ToListAsync();

        if (oldCompletedJobs.Count > 0)
        {
            _logger.LogInformation($"Removing {oldCompletedJobs.Count} completed jobs older than 30 days from database");
            db.Jobs.RemoveRange(oldCompletedJobs);
            await db.SaveChangesAsync();
        }
    }

    private async Task CheckForStuckRunners(List<GithubTargetConfiguration> targetConfig)
    {
        // check the database for runners that are in "created" state for more then 5 minutes.
        
        await using var db = new ActionsRunnerContext();
        var cutoffTime = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        
        // Query stuck runners by joining with lifecycle table
        var stuckRunners = await db.Runners
            .AsNoTracking()
            .Where(r => db.RunnerLifecycles
                .Where(rl => rl.RunnerId == r.RunnerId && rl.Status == RunnerStatus.Created)
                .Any(rl => rl.EventTimeUtc < cutoffTime) &&
                db.RunnerLifecycles
                .Where(rl => rl.RunnerId == r.RunnerId)
                .OrderByDescending(rl => rl.EventTimeUtc)
                .First().Status == RunnerStatus.Created)
            .Select(r => new { r.RunnerId, r.CloudServerId, r.Hostname, r.Cloud })
            .ToListAsync();
        
        if (stuckRunners.Count == 0)
            return;
        
        // Process stuck runners and create lifecycle entries
        var lifecycleEntries = new List<RunnerLifecycle>();
        
        foreach(var stuckRunner in stuckRunners)
        {
            // Add to deletion queue
            _queues.DeleteTasks.Enqueue(new DeleteRunnerTask
            {
                ServerId = stuckRunner.CloudServerId,
                RunnerDbId = stuckRunner.RunnerId
            });
            
            // Create lifecycle entry for batch insert
            lifecycleEntries.Add(new RunnerLifecycle
            {
                RunnerId = stuckRunner.RunnerId,
                Event = "Stuck in provisioning. Killing.",
                EventTimeUtc = DateTime.UtcNow,
                Status = RunnerStatus.Failure
            });
            
            _logger.LogWarning($"Killing Runner stuck in provisioning: {stuckRunner.Hostname} on {stuckRunner.Cloud}");
        }
        
        // Batch insert lifecycle entries without change tracking
        if (lifecycleEntries.Count > 0)
        {
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            db.RunnerLifecycles.AddRange(lifecycleEntries);
            await db.SaveChangesAsync();
        }
    }

    private async Task ProcessStats(List<GithubTargetConfiguration> targetConfig)
    {
        CreateQueueSize.Set(_queues.CreateTasks.Count);
        DeleteQueueSize.Set(_queues.DeleteTasks.Count);
        ProvisionQueueSize.Set(_queues.CreatedRunners.Count);
        
        foreach(ICloudController cc in _cc)
        {
            try
            {
                CspRunnerCount.Labels(cc.CloudIdentifier).Set(await cc.GetServerCountFromCsp());
            }
            catch(Exception ex)
            {
                _logger.LogWarning($"Unable to get runner count from CSP {cc.CloudIdentifier}: {ex.GetFullExceptionDetails()}");
                SentrySdk.CaptureException(ex, scope =>
                {
                    scope.SetTag("csp", cc.CloudIdentifier);
                });
            }
        }
        
        
        // Grab job state counts
        var db = new ActionsRunnerContext();
        var stuckTime = DateTime.UtcNow - TimeSpan.FromMinutes(15);

        // Count stuck jobs (queued for >15min, excluding throttled jobs)
        var stuckJobs = await db.Jobs.CountAsync(x => x.State == JobState.Queued && x.RunnerId == null && x.QueueTime < stuckTime);
        StuckJobsCount.Set(stuckJobs);

        // Count throttled jobs
        var throttledJobs = await db.Jobs.CountAsync(x => x.State == JobState.Throttled);
        ThrottledJobsCount.Set(throttledJobs);

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
            SentrySdk.CaptureException(ex);
            _logger.LogError($"Unable to get stats: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if the runner quota has been reached for the given owner
    /// </summary>
    /// <param name="owner">The GitHub target configuration</param>
    /// <param name="db">Database context</param>
    /// <returns>True if quota is reached and no more runners should be created, false otherwise</returns>
    private async Task<bool> IsQuotaReached(GithubTargetConfiguration owner, ActionsRunnerContext db)
    {
        // If no quota is set, unlimited runners are allowed
        if (!owner.RunnerQuota.HasValue)
        {
            return false;
        }

        // Count all runners that are actively consuming resources (not deleted/failed/cancelled)
        // This includes: CreationQueued, Created, Provisioned, Processing (states 1-4)
        // Excludes: DeletionQueued, Deleted, Failure, VanishedOnCloud, Cleanup, Cancelled (states 5+)
        var runners = await db.Runners
            .Include(x => x.Lifecycle)
            .Where(x => x.Owner == owner.Name)
            .ToListAsync();

        int currentRunnerCount = runners.Count(x => x.LastState < RunnerStatus.DeletionQueued);

        bool quotaReached = currentRunnerCount >= owner.RunnerQuota.Value;

        if (quotaReached)
        {
            _logger.LogWarning($"Runner quota reached for {owner.Name}: {currentRunnerCount}/{owner.RunnerQuota.Value} (includes queued/provisioning runners)");
        }

        return quotaReached;
    }

    private async Task StartPoolRunners(List<GithubTargetConfiguration> targetConfig)
    {
        // Start pool runners
        var db = new ActionsRunnerContext();
        foreach (GithubTargetConfiguration owner in targetConfig)
        {
            _logger.LogInformation($"Checking pool runners for {owner.Name}");

            // Check if quota is reached for this owner
            if (await IsQuotaReached(owner, db))
            {
                _logger.LogWarning($"Skipping pool runner creation for {owner.Name} - quota reached");
                continue;
            }

            List<Runner> existingRunners = await db.Runners.Where(x => x.Owner == owner.Name && x.IsOnline).ToListAsync();

            foreach (Pool pool in owner.Pools)
            {
                int existCt = existingRunners.Count(x => x.Size == pool.Size);
                int missingCt = pool.NumRunners - existCt;

                string arch = Program.Config.Sizes.FirstOrDefault(x => x.Name == pool.Size)?.Arch;

                _logger.LogInformation($"Checking pool {pool.Size} [{arch}]: Existing={existCt} Requested={pool.NumRunners} Missing={missingCt}");

                for (int i = 0; i < missingCt; i++)
                {
                    // Check quota again before each runner creation
                    if (await IsQuotaReached(owner, db))
                    {
                        _logger.LogWarning($"Quota reached while creating pool runners for {owner.Name} - stopping at {i}/{missingCt}");
                        break;
                    }

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
                        RepoName = owner.Name,
                        TargetType = owner.Target,
                        RunnerDbId = newRunner.RunnerId
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

        // Check both Queued and Throttled jobs that have been waiting >10min without a runner
        var stuckJobs = await db.Jobs
            .Where(x => (x.State == JobState.Queued || x.State == JobState.Throttled) && x.RunnerId == null && x.QueueTime < stuckTime)
            .ToListAsync();

        foreach (var stuckJob in stuckJobs)
        {
            var owner = targetConfig.FirstOrDefault(x => x.Name == stuckJob.Owner);
            if (owner == null)
            {
                _logger.LogError($"Unable to get owner for stuck job. {stuckJob.JobId}");
                continue;
            }

            // Check if quota is reached
            bool quotaReached = await IsQuotaReached(owner, db);

            if (quotaReached)
            {
                // Mark job as Throttled if it isn't already
                if (stuckJob.State != JobState.Throttled)
                {
                    _logger.LogInformation($"Job {stuckJob.JobId} in {stuckJob.Repository} is throttled due to runner quota limit.");
                    stuckJob.State = JobState.Throttled;
                    await db.SaveChangesAsync();
                }
                continue;
            }
            else
            {
                // Quota is available - if job was throttled, move it back to queued
                if (stuckJob.State == JobState.Throttled)
                {
                    _logger.LogInformation($"Quota now available for throttled job {stuckJob.JobId}. Moving to queued state.");
                    stuckJob.State = JobState.Queued;
                    await db.SaveChangesAsync();
                }
            }

            // Job is genuinely stuck (not due to quota) - create replacement runner
            _logger.LogWarning($"Found stuck Job: {stuckJob.JobId} in {stuckJob.Repository}. Starting new runner to compensate...");

            // Check if there is already a runner in queue to unstuck
            if (_queues.CreateTasks.Any(x => x.IsStuckReplacement && x.StuckJobId == stuckJob.JobId))
            {
                _logger.LogWarning($"Creating queue already has a task for jobs {stuckJob.JobId}");
                continue;
            }

            int replacementsInQueue =  _queues.CreateTasks.CountWhere(x => x.IsStuckReplacement);
            if (replacementsInQueue > 25)
            {
                _logger.LogWarning($"Creating queue already has {replacementsInQueue} stuck jobs replacements. Not adding more strain.");
                continue;
            }
            
            // check job on github
            GitHubApiWorkflowRun ghJob = await GitHubApi.GetJobInfoForRepo(stuckJob.GithubJobId, stuckJob.Repository , owner.GitHubToken);
            if (ghJob == null || ghJob.Status != "queued")
            {
                _logger.LogWarning($"job info for {stuckJob.JobId} not found or job not queued anymore on github.");

                if (ghJob == null)
                {
                    _logger.LogWarning($"GHjob for {stuckJob.JobId} is null");
                }
                else if (ghJob.Status == "completed")
                {
                    _logger.LogWarning($"GHjob status for {stuckJob.JobId} is {ghJob.Status} - Marking job accordingly");
                    stuckJob.State = JobState.Completed;
                    stuckJob.CompleteTime = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }
                else if (ghJob.Status != "queued")
                {
                    _logger.LogWarning($"GHjob status for {stuckJob.JobId} is {ghJob.Status}");
                }
                
                if (stuckJob.QueueTime + TimeSpan.FromHours(2) > DateTime.UtcNow)
                {
                    _logger.LogWarning($"Marking stuck job {stuckJob.GithubJobId} vanished as it's no longer in the GitHub queued state for more than 2h.");
                    stuckJob.State = JobState.Vanished;
                    stuckJob.CompleteTime = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }
                
                continue;
            }

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
                Owner = stuckJob.Owner,
                StuckJobReplacement = true
                
            };
            await db.Runners.AddAsync(newRunner);
            await db.SaveChangesAsync();
           
            _queues.CreateTasks.Enqueue(new CreateRunnerTask
            {
                RepoName = stuckJob.Repository,
                TargetType = owner.Target,
                RunnerDbId = newRunner.RunnerId,
                IsStuckReplacement = true,
                StuckJobId = stuckJob.JobId
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

                if (DateTime.UtcNow - runner.CreationQueuedTime < TimeSpan.FromMinutes(30))
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

                if (DateTime.UtcNow - runner.CreationQueuedTime < TimeSpan.FromHours(6))
                {
                    // VM younger than 6h - not cleaning yet
                    continue;
                }

                if (runner.LastState == RunnerStatus.Processing)
                {
                    // VM still processing - not cleaning
                    _logger.LogWarning($"Found a long processing runner: {runner.Hostname}");
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
        foreach (ICloudController cc in _cc)
        {
            try
            {
                List<CspServer> remainingServers = await cc.GetAllServersFromCsp();
                foreach (CspServer cspServer in remainingServers)
                {
                    
                    SentrySdk.AddBreadcrumb("Checking server for removal.", category: "Cleanup", data: new Dictionary<string, string>
                    {
                        {"server", cspServer.Name}
                    });
                    if (registeredServerNames.Contains(cspServer.Name))
                    {
                        // If we know the server in github, skip
                        continue;
                    }

                    if (cspServer.CreatedAt + TimeSpan.FromMinutes(5) > DateTime.UtcNow)
                    {
                        // fresh runner. don't act on it yet
                        continue;
                    }

                    // Check for a runner existing longer than 24h that might have vanished from github but is still processing
                    var cspRunnerDb = await db.Runners.Include(x => x.Job).FirstOrDefaultAsync(x => x.Hostname == cspServer.Name);
                    if (cspRunnerDb is { Job: not null })
                    {
                        try
                        {
                            // query github for job
                            var runnerToken = Program.Config.TargetConfigs.FirstOrDefault(x => x.Name == cspRunnerDb.Job.Owner).GitHubToken;
                            var githubJob = await GitHubApi.GetJobInfoForOrg(cspRunnerDb.Job.GithubJobId, cspRunnerDb.Job.Repository, runnerToken);
                            if (githubJob is { Status: "running" })
                            {
                                _logger.LogWarning($"Got a runner ({cspServer.Name}) not in GitHub anymore but still processing. Indicates a >24h job.");
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            SentrySdk.CaptureException(ex, scope =>
                            {
                                scope.Level = SentryLevel.Warning;
                            });
                        }

                    }
                    

                    _logger.LogInformation($"{cspServer.Name} is a candidate to be killed from {cc.CloudIdentifier}");

                    var runner = await db.Runners.Include(x => x.Lifecycle).FirstOrDefaultAsync(x => x.Hostname == cspServer.Name);
                    if (runner == null)
                    {
                        _logger.LogInformation($"{cspServer.Name} is not found in the database");
                        continue;
                    }

                    if (runner.Lifecycle.Count(x => x.Status == RunnerStatus.DeletionQueued) > 10)
                    {
                        runner.Lifecycle.Add(new()
                        {
                            Status = RunnerStatus.DeletionQueued,
                            Event = "Still around after going in deletion queue. trying again...",
                            EventTimeUtc = DateTime.UtcNow
                        });
                        await db.SaveChangesAsync();
                        _queues.DeleteTasks.Enqueue(new()
                        {
                            RunnerDbId = runner.RunnerId,
                            ServerId = cspServer.Id
                        });

                    }
                    else if (runner.Lifecycle.Any(x => x.Status == RunnerStatus.DeletionQueued))
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
                             (runner.LastState != RunnerStatus.Processing && DateTime.UtcNow - cspServer.CreatedAt.ToUniversalTime() > TimeSpan.FromMinutes(40)))
                    {
                        _logger.LogInformation($"Removing VM that is not in any GitHub registration: {cspServer.Name} created at {cspServer.CreatedAt:u}");
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
                            ServerId = cspServer.Id
                        });

                    }

                }

            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                _logger.LogError($"Failed during cleanup from CSP {cc.CloudIdentifier}: {ex.Message}");
            }
        }

        foreach (var onlineSrvFromDb in db.Runners.Include(x => x.Lifecycle).Where(x => x.IsOnline))
        {
            if(onlineSrvFromDb.CreationQueuedTime + TimeSpan.FromHours(1) > DateTime.UtcNow ) continue; // Leave young runners alone
            if (registeredServerNames.Contains(onlineSrvFromDb.Hostname)) continue;
           
            if(onlineSrvFromDb.LastState == RunnerStatus.DeletionQueued) continue;
            
            
            _logger.LogWarning($"Runner {onlineSrvFromDb.Hostname} is marked online but not registered in GitHub. Marking offline.");
            onlineSrvFromDb.Lifecycle.Add(new()
            {
                Status = RunnerStatus.VanishedOnCloud,
                Event = "Marking VM as offline. Vanished from system.",
                EventTimeUtc = DateTime.UtcNow
            });
            onlineSrvFromDb.IsOnline = false;

        }
        await db.SaveChangesAsync();

    }

    private async Task<bool> DeleteRunner(DeleteRunnerTask rt)
    {
        var db = new ActionsRunnerContext();
        var runner = await db.Runners.Include(x => x.Lifecycle).FirstOrDefaultAsync(x => x.RunnerId == rt.RunnerDbId);
        
        try
        {
            ICloudController cc = _cc.FirstOrDefault(x => x.CloudIdentifier == runner.Cloud);
            if (cc == null)
            {
                throw new NullReferenceException($"No Cloud controller found for runner {runner.Cloud}");
            }
            await cc.DeleteRunner(rt.ServerId);
            runner.IsOnline = false;
            runner.Lifecycle.Add(new()
            {
                Status = RunnerStatus.Deleted,
                EventTimeUtc = DateTime.UtcNow,
                Event = "Runner was successfully deleted from CSP"
            });
            await db.SaveChangesAsync();

            return true;
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex, scope =>
            {
                scope.SetTag("server-id", rt.ServerId.ToString());
            });
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

        // Check if this runner creation should be skipped due to job cancellation
        var key = (runner.Owner, rt.RepoName, runner.Size, runner.Profile, runner.Arch);
        bool shouldSkip = false;

        _queues.CancelledRunners.AddOrUpdate(
            key,
            0,  // If key doesn't exist, don't skip
            (k, count) =>
            {
                if (count > 0)
                {
                    shouldSkip = true;
                    return count - 1;  // Decrement counter
                }
                return 0;
            });

        if (shouldSkip)
        {
            _logger.LogInformation($"Skipping runner creation for cancelled job: Owner={runner.Owner}, Repo={rt.RepoName}, Size={runner.Size}, Profile={runner.Profile}, Arch={runner.Arch}");

            runner.Lifecycle.Add(new RunnerLifecycle
            {
                Status = RunnerStatus.Cancelled,
                EventTimeUtc = DateTime.UtcNow,
                Event = "Runner creation skipped - job was cancelled"
            });

            await db.SaveChangesAsync();
            return true;
        }

        // Check if cloud is stable atm
        
        var possibleProviders =
            Program.Config.Sizes.FirstOrDefault(x => x.Name == runner.Size && x.Arch == runner.Arch)?.VmTypes;

        if (possibleProviders == null)
        {
            _logger.LogError($"No VM provider found for runner {runner.Size}/{runner.Arch}");
            return false;
        }

        var selectedProvider = possibleProviders
            .Where(x => !_bannedClouds.Any(y => y.Cloud == x.Cloud && y.Size == runner.Size))
            .OrderByDescending(x => x.Priority)
            .FirstOrDefault();

        if (selectedProvider == null)
        {
            _logger.LogError($"No VM provider available for runner {runner.Size}/{runner.Arch}");
            return false;
        }
        
        var cc = _cc.First(x => x.CloudIdentifier == selectedProvider.Cloud);

        // Generate fresh runner token just before creating the runner
        var targetConfig = Program.Config.TargetConfigs.FirstOrDefault(x => x.Name == runner.Owner);
        if (targetConfig == null)
        {
            _logger.LogError($"Unable to find target configuration for owner: {runner.Owner}");
            return false;
        }

        string runnerToken = rt.TargetType switch
        {
            TargetType.Repository => await GitHubApi.GetRunnerTokenForRepo(targetConfig.GitHubToken, targetConfig.Name),
            TargetType.Organization => await GitHubApi.GetRunnerTokenForOrg(targetConfig.GitHubToken, targetConfig.Name),
            _ => throw new ArgumentOutOfRangeException()
        };

        if (string.IsNullOrEmpty(runnerToken))
        {
            _logger.LogError($"Unable to generate runner token for {runner.Owner}");
            return false;
        }

        try
        {
            string targetName = rt.TargetType switch
            {
                TargetType.Repository => rt.RepoName,
                TargetType.Organization => runner.Owner,
                _ => throw new ArgumentOutOfRangeException()
            };


            Machine newRunner;
            int retryAttempt = 0;
            const int maxRetries = 1;
            const int retryDelayMs = 1000; // 1 second delay between retries

            while (retryAttempt <= maxRetries)
            {

                try
                {
                    newRunner = await cc.CreateNewRunner(runner.Arch, runner.Size, runnerToken, targetName, runner.IsCustom, runner.Profile);
                    _logger.LogInformation($"New Runner {newRunner.Name} [{runner.Size} on {runner.Arch}] entering pool for {targetName}.");
                    MachineCreatedCount.Labels(runner.Owner, runner.Size).Inc();

                    runner.Hostname = newRunner.Name;
                    runner.IsOnline = true;
                    runner.CloudServerId = newRunner.Id;
                    runner.IPv4 = newRunner.Ipv4;
                    runner.Cloud = cc.CloudIdentifier;
                    runner.ProvisionId = newRunner.ProvisionId;
                    runner.ProvisionPayload = newRunner.ProvisionPayload;
                    runner.Lifecycle.Add(new RunnerLifecycle
                    {
                        Status = RunnerStatus.Created,
                        EventTimeUtc = DateTime.UtcNow,
                        Event = $"New Runner {newRunner.Name} [{runner.Size} on {runner.Arch}] entering pool for {targetName}."
                    });
                    break;
                }
                catch (Exception ex)
                {
                    if (retryAttempt == maxRetries)
                    {
                        _logger.LogError(ex, $"Failed to create runner after {maxRetries + 1} attempts");
                        throw; // Re-throw the exception after all retries are exhausted
                    }
        
                    _logger.LogWarning(ex, $"Failed to create runner (attempt {retryAttempt + 1}/{maxRetries + 1}). Retrying...");
                    await Task.Delay(retryDelayMs);
                    retryAttempt++; 
                }
            }
            await db.SaveChangesAsync();

            _queues.CreatedRunners.TryAdd(runner.Hostname, rt);
            
            return true;
        }
        catch (Exception ex)
        {
            SentrySdk.CaptureException(ex, scope =>
            {
                scope.SetTag("runner-size", runner.Size);
                scope.SetTag("runner-arch", runner.Arch);
            });
            _logger.LogError($"Unable to create runner [{runner.Size} on {runner.Arch} | Retry: {rt.RetryCount}]: {ex.GetFullExceptionDetails()}");
            runner.Lifecycle.Add(new RunnerLifecycle
            {
                Status = RunnerStatus.Failure,
                EventTimeUtc = DateTime.UtcNow,
                Event = $"Unable to create runner [{runner.Size} on {runner.Arch} | Retry: {rt.RetryCount}]: {ex.Message}"
            });
            rt.RetryCount += 1;
            // Don't retry stuck job runners - the stuck job detector will create retry servers
            if (rt.RetryCount < 3 && !runner.StuckJobReplacement)
            {
                _queues.CreateTasks.Enqueue(rt);
            }
            else
            {
                _logger.LogError(runner.StuckJobReplacement ? $"Retries exceeded for {runner.Size} on {runner.Arch}. giving up. (Stuck job replacement)" : $"Retries exceeded for {runner.Size} on {runner.Arch}. giving up.");
                runner.Lifecycle.Add(new RunnerLifecycle
                {
                    Status = RunnerStatus.Failure,
                    EventTimeUtc = DateTime.UtcNow,
                    Event = $"Retries exceeded for {runner.Size} on {runner.Arch}. giving up."
                });
            }
            await db.SaveChangesAsync();
            
            // Ban size on cloud for 30min
            _bannedClouds.Add(new CloudBan()
            {
                Cloud = cc.CloudIdentifier,
                Size = runner.Size,
                UnbanTime = DateTime.UtcNow + TimeSpan.FromMinutes(10) 
            });

            return false;
        }
    }
}