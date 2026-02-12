using System.Diagnostics;
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
    private static readonly Gauge DanglingRunnersCount = Metrics
        .CreateGauge("github_autoscaler_runners_dangling", "Number of provisioned runners that never picked up a job");
    private static readonly Gauge RunnersWithCompletedJobsCount = Metrics
        .CreateGauge("github_autoscaler_runners_completed_jobs", "Number of runners with completed jobs awaiting cleanup");

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
        using var activity = Program.OrchestratorActivitySource.StartActivity("maintenance.check_stuck_runners");

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
        
        activity?.SetTag("stuck_runners.count", stuckRunners.Count);

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
        await using var db = new ActionsRunnerContext();
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

        // Calculate dangling runners metric - idle runners with no job assignment older than 30 min
        var danglingCutoff = DateTime.UtcNow - TimeSpan.FromMinutes(30);
        var danglingRunners = await db.Runners
            .Where(r => r.IsOnline &&
                        r.JobId == null &&
                        db.RunnerLifecycles
                            .Any(rl => rl.RunnerId == r.RunnerId && rl.Status == RunnerStatus.CreationQueued && rl.EventTimeUtc < danglingCutoff))
            .CountAsync();
        DanglingRunnersCount.Set(danglingRunners);

        // Calculate runners with completed jobs
        var runnersWithCompletedJobs = await db.Runners
            .Where(r => r.IsOnline &&
                        r.JobId != null &&
                        (r.Job.State == JobState.Completed || r.Job.State == JobState.Cancelled || r.Job.State == JobState.Vanished))
            .CountAsync();
        RunnersWithCompletedJobsCount.Set(runnersWithCompletedJobs);

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
        await using var db = new ActionsRunnerContext();
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
        using var activity = Program.OrchestratorActivitySource.StartActivity("maintenance.check_stuck_jobs");

        await using var db = new ActionsRunnerContext();
        var stuckTime = DateTime.UtcNow - TimeSpan.FromMinutes(10);

        // Check both Queued and Throttled jobs that have been waiting >10min without a runner
        var stuckJobs = await db.Jobs
            .Where(x => (x.State == JobState.Queued || x.State == JobState.Throttled) && x.RunnerId == null && x.QueueTime < stuckTime)
            .ToListAsync();

        activity?.SetTag("stuck_jobs.count", stuckJobs.Count);

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

                    if (stuckJob.QueueTime + TimeSpan.FromHours(2) < DateTime.UtcNow)
                    {
                        _logger.LogWarning($"Marking stuck job {stuckJob.GithubJobId} vanished as it's no longer in the GitHub queued state for more than 2h.");
                        stuckJob.State = JobState.Vanished;
                        stuckJob.CompleteTime = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                    }
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

    // Helper methods for cleanup process

    /// <summary>
    /// Gets the creation time for a runner. Prefers CreatedTime from database, falls back to CSP time.
    /// Automatically loads Lifecycle if not already loaded.
    /// </summary>
    private async Task<DateTime> GetRunnerCreationTime(Runner runner, CspServer cspServer = null, ActionsRunnerContext db = null)
    {
        // Ensure Lifecycle is loaded
        if (runner.Lifecycle == null || !runner.Lifecycle.Any())
        {
            if (db != null)
            {
                try
                {
                    await db.Entry(runner).Collection(r => r.Lifecycle).LoadAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Unable to load Lifecycle for runner {runner.RunnerId}: {ex.Message}");
                }
            }
            else
            {
                _logger.LogWarning($"GetRunnerCreationTime called without Lifecycle loaded and no DbContext provided for runner {runner.RunnerId}");
            }
        }

        // First choice: Use the Created lifecycle event time (when VM was actually created)
        if (runner.Lifecycle?.Any() == true && runner.CreatedTime != DateTime.MaxValue)
            return runner.CreatedTime;

        // Fallback: Use CSP creation time if available
        if (cspServer != null)
            return cspServer.CreatedAt.ToUniversalTime();

        // Last resort: Use queue time if lifecycle is available
        if (runner.Lifecycle?.Any() == true)
            return runner.CreationQueuedTime;

        // Ultimate fallback: current time minus 1 hour (safe default)
        _logger.LogWarning($"Unable to determine creation time for runner {runner.RunnerId} - using default");
        return DateTime.UtcNow - TimeSpan.FromHours(1);
    }

    /// <summary>
    /// Checks if a runner's job is complete by checking database state and optionally GitHub API.
    /// </summary>
    private bool IsJobComplete(Runner runner, GitHubApiWorkflowRun githubJob = null)
    {
        // No job assigned = never processed = can cleanup
        if (runner.Job == null)
            return true;

        // Check database job state
        if (runner.Job.State == JobState.Completed ||
            runner.Job.State == JobState.Cancelled ||
            runner.Job.State == JobState.Vanished)
            return true;

        // Cross-check with GitHub API if available
        if (githubJob != null &&
            (githubJob.Status == "completed" ||
             githubJob.Conclusion != null))
            return true;

        return false;
    }

    /// <summary>
    /// Checks if it's safe to queue a runner for deletion (not already queued recently).
    /// </summary>
    private bool SafeToQueueDeletion(Runner runner)
    {
        // Check for recent deletion queue events (within last 5 minutes)
        var recentDeletionQueue = runner.Lifecycle
            .Where(x => x.Status == RunnerStatus.DeletionQueued)
            .OrderByDescending(x => x.EventTimeUtc)
            .FirstOrDefault();

        if (recentDeletionQueue != null)
        {
            // If queued within last 5 minutes, don't re-queue
            if (DateTime.UtcNow - recentDeletionQueue.EventTimeUtc < TimeSpan.FromMinutes(5))
            {
                return false;
            }

            // If queued more than 10 times, something is wrong - force retry
            int queueCount = runner.Lifecycle.Count(x => x.Status == RunnerStatus.DeletionQueued);
            if (queueCount > 10)
            {
                _logger.LogError($"Runner {runner.Hostname} has been queued {queueCount} times for deletion - forcing retry");
                return true;
            }
        }

        return true;
    }

    /// <summary>
    /// Final safety check before deletion - ensures runner is not actively working.
    /// </summary>
    private async Task<bool> IsSafeToDelete(Runner runner, GitHubRunner ghRunner = null, ActionsRunnerContext db = null)
    {
        // NEVER delete if currently processing
        if (runner.LastState == RunnerStatus.Processing)
            return false;

        // NEVER delete if job is in progress
        if (runner.Job != null && runner.Job.State == JobState.InProgress)
            return false;

        // NEVER delete if GitHub shows busy
        if (ghRunner != null && ghRunner.Busy)
            return false;

        // NEVER delete very fresh runners (< 5 minutes) - protect against race conditions
        var age = DateTime.UtcNow - await GetRunnerCreationTime(runner, null, db);
        if (age < TimeSpan.FromMinutes(5))
            return false;

        return true;
    }

    private async Task CleanUpRunners(List<GithubTargetConfiguration> targetConfigs)
    {
        using var activity = Program.OrchestratorActivitySource.StartActivity("maintenance.cleanup_runners");

        List<string> registeredServerNames = new();
        await using var db = new ActionsRunnerContext();

        foreach (GithubTargetConfiguration githubTarget in targetConfigs)
        {
            _logger.LogInformation($"Cleaning runners for {githubTarget.Name}...");

            // Get runner info from GitHub
            List<GitHubRunner> githubRunners = githubTarget.Target switch
            {
                TargetType.Organization => await GitHubApi.GetRunnersForOrg(githubTarget.GitHubToken, githubTarget.Name),
                TargetType.Repository => await GitHubApi.GetRunnersForRepo(githubTarget.GitHubToken, githubTarget.Name),
                _ => throw new ArgumentOutOfRangeException()
            };

            // CATEGORY 1: Offline Runners (Already Dead)
            // These runners are offline on GitHub - quick cleanup after 10 minutes
            List<GitHubRunner> ghOfflineRunners = githubRunners
                .Where(x => x.Name.StartsWith(Program.Config.RunnerPrefix) && x.Status == "offline")
                .ToList();

            foreach (GitHubRunner ghRunner in ghOfflineRunners)
            {
                var runner = await db.Runners.Include(x => x.Lifecycle).Include(x => x.Job).FirstOrDefaultAsync(x => x.Hostname == ghRunner.Name);

                if (runner == null)
                {
                    // Orphaned GitHub runner - remove immediately
                    _logger.LogWarning($"Found offline runner on GitHub not in database: {ghRunner.Name} - Removing");
                    _ = githubTarget.Target switch
                    {
                        TargetType.Organization => await GitHubApi.RemoveRunnerFromOrg(githubTarget.Name, githubTarget.GitHubToken, ghRunner.Id),
                        TargetType.Repository => await GitHubApi.RemoveRunnerFromRepo(githubTarget.Name, githubTarget.GitHubToken, ghRunner.Id),
                        _ => throw new ArgumentOutOfRangeException()
                    };
                    continue;
                }

                // Protection: Must be at least 10 minutes old
                var runnerAge = DateTime.UtcNow - await GetRunnerCreationTime(runner, null, db);
                if (runnerAge < TimeSpan.FromMinutes(10))
                {
                    continue;
                }

                // Protection: Never delete if still processing
                if (runner.LastState == RunnerStatus.Processing)
                {
                    _logger.LogWarning($"Runner {runner.Hostname} is offline but in Processing state - protecting");
                    continue;
                }

                // Protection: Check safety before deletion
                if (!await IsSafeToDelete(runner, ghRunner, db))
                {
                    continue;
                }

                _logger.LogInformation($"[OFFLINE] Removing offline runner {ghRunner.Name} (age: {runnerAge.TotalMinutes:F1} min)");

                // Remove from GitHub
                _ = githubTarget.Target switch
                {
                    TargetType.Organization => await GitHubApi.RemoveRunnerFromOrg(githubTarget.Name, githubTarget.GitHubToken, ghRunner.Id),
                    TargetType.Repository => await GitHubApi.RemoveRunnerFromRepo(githubTarget.Name, githubTarget.GitHubToken, ghRunner.Id),
                    _ => throw new ArgumentOutOfRangeException()
                };

                // Mark offline and queue deletion
                runner.IsOnline = false;
                if (SafeToQueueDeletion(runner))
                {
                    runner.Lifecycle.Add(new()
                    {
                        Status = RunnerStatus.DeletionQueued,
                        EventTimeUtc = DateTime.UtcNow,
                        Event = $"Offline runner cleanup - age: {runnerAge.TotalMinutes:F1} min"
                    });
                    await db.SaveChangesAsync();

                    _queues.DeleteTasks.Enqueue(new()
                    {
                        ServerId = runner.CloudServerId,
                        RunnerDbId = runner.RunnerId
                    });
                }
                else
                {
                    await db.SaveChangesAsync();
                    _logger.LogDebug($"Skipping deletion queue for {runner.Hostname} - recently queued");
                }
            }

            // CATEGORY 2: Runners with Completed Jobs
            // If a job is complete, the runner should be removed quickly (5 min grace)
            List<GitHubRunner> ghOnlineRunners = githubRunners
                .Where(x => x.Name.StartsWith(Program.Config.RunnerPrefix) && x.Status == "online")
                .ToList();

            foreach (GitHubRunner ghRunner in ghOnlineRunners)
            {
                var runner = await db.Runners.Include(x => x.Lifecycle).Include(x => x.Job).FirstOrDefaultAsync(x => x.Hostname == ghRunner.Name);

                if (runner == null || runner.Job == null)
                {
                    continue;
                }

                // Check if job is completed
                if (!IsJobComplete(runner))
                {
                    continue;
                }

                // Protection: 5 minute grace period after job completion
                var completionTime = runner.Job.CompleteTime;
                if (completionTime != DateTime.MinValue && DateTime.UtcNow - completionTime < TimeSpan.FromMinutes(5))
                {
                    continue;
                }

                // Protection: Check safety
                if (!await IsSafeToDelete(runner, ghRunner, db))
                {
                    continue;
                }

                _logger.LogInformation($"[COMPLETED JOB] Removing runner {ghRunner.Name} - job {runner.Job.JobId} is complete");

                // Remove from GitHub
                _ = githubTarget.Target switch
                {
                    TargetType.Organization => await GitHubApi.RemoveRunnerFromOrg(githubTarget.Name, githubTarget.GitHubToken, ghRunner.Id),
                    TargetType.Repository => await GitHubApi.RemoveRunnerFromRepo(githubTarget.Name, githubTarget.GitHubToken, ghRunner.Id),
                    _ => throw new ArgumentOutOfRangeException()
                };

                runner.IsOnline = false;
                if (SafeToQueueDeletion(runner))
                {
                    runner.Lifecycle.Add(new()
                    {
                        Status = RunnerStatus.DeletionQueued,
                        EventTimeUtc = DateTime.UtcNow,
                        Event = $"Job {runner.Job.JobId} completed - removing runner"
                    });
                    await db.SaveChangesAsync();

                    _queues.DeleteTasks.Enqueue(new()
                    {
                        ServerId = runner.CloudServerId,
                        RunnerDbId = runner.RunnerId
                    });
                }
                else
                {
                    await db.SaveChangesAsync();
                    _logger.LogDebug($"Skipping deletion queue for {runner.Hostname} - recently queued");
                }
            }

            // CATEGORY 3: Dangling Runners (Never Picked Up Job)
            // Runners that are idle and haven't picked up a job - 30 min threshold
            // Note: Runners with completed jobs are handled by Category 2
            List<GitHubRunner> ghIdleRunners = githubRunners
                .Where(x => x.Name.StartsWith(Program.Config.RunnerPrefix) && x.Status == "online" && !x.Busy)
                .ToList();

            foreach (GitHubRunner ghRunner in ghIdleRunners)
            {
                var runner = await db.Runners.Include(x => x.Lifecycle).Include(x => x.Job).FirstOrDefaultAsync(x => x.Hostname == ghRunner.Name);

                if (runner == null)
                {
                    continue;
                }

                // Skip if this runner has a job assignment - Category 2 handles completed jobs
                if (runner.Job != null)
                {
                    continue;
                }

                // DANGLING RUNNER: Idle with no job assignment
                var runnerAge = DateTime.UtcNow - await GetRunnerCreationTime(runner, null, db);

                // Protection: Must be at least 30 minutes old
                if (runnerAge < TimeSpan.FromMinutes(30))
                {
                    continue;
                }

                // Protection: Check safety
                if (!await IsSafeToDelete(runner, ghRunner, db))
                {
                    continue;
                }

                _logger.LogWarning($"[DANGLING] Removing dangling runner {ghRunner.Name} - idle for {runnerAge.TotalMinutes:F1} min with no job assignment");

                // Remove from GitHub
                _ = githubTarget.Target switch
                {
                    TargetType.Organization => await GitHubApi.RemoveRunnerFromOrg(githubTarget.Name, githubTarget.GitHubToken, ghRunner.Id),
                    TargetType.Repository => await GitHubApi.RemoveRunnerFromRepo(githubTarget.Name, githubTarget.GitHubToken, ghRunner.Id),
                    _ => throw new ArgumentOutOfRangeException()
                };

                runner.IsOnline = false;
                if (SafeToQueueDeletion(runner))
                {
                    runner.Lifecycle.Add(new()
                    {
                        Status = RunnerStatus.DeletionQueued,
                        EventTimeUtc = DateTime.UtcNow,
                        Event = $"Dangling runner - idle for {runnerAge.TotalMinutes:F1} min with no job assignment"
                    });
                    await db.SaveChangesAsync();

                    _queues.DeleteTasks.Enqueue(new()
                    {
                        ServerId = runner.CloudServerId,
                        RunnerDbId = runner.RunnerId
                    });
                }
                else
                {
                    await db.SaveChangesAsync();
                    _logger.LogDebug($"Skipping deletion queue for {runner.Hostname} - recently queued");
                }
            }

            // Collect registered runner names for CSP cleanup
            githubRunners = githubTarget.Target switch
            {
                TargetType.Organization => await GitHubApi.GetRunnersForOrg(githubTarget.GitHubToken, githubTarget.Name),
                TargetType.Repository => await GitHubApi.GetRunnersForRepo(githubTarget.GitHubToken, githubTarget.Name),
                _ => throw new ArgumentOutOfRangeException()
            };
            registeredServerNames.AddRange(githubRunners.Where(x => x.Name.StartsWith(Program.Config.RunnerPrefix)).Select(x => x.Name));
        }

        // CATEGORY 4: CSP Cleanup - Remove VMs not registered in GitHub
        // Handles runners that vanished from GitHub or never registered
        foreach (ICloudController cc in _cc)
        {
            try
            {
                List<CspServer> cspServers = await cc.GetAllServersFromCsp();

                foreach (CspServer cspServer in cspServers)
                {
                    SentrySdk.AddBreadcrumb("Checking CSP server for removal", category: "Cleanup", data: new Dictionary<string, string>
                    {
                        {"server", cspServer.Name}
                    });

                    // Skip if registered in GitHub
                    if (registeredServerNames.Contains(cspServer.Name))
                    {
                        continue;
                    }

                    // Protection: 5-minute grace for fresh VMs (registration time)
                    if (cspServer.CreatedAt + TimeSpan.FromMinutes(5) > DateTime.UtcNow)
                    {
                        continue;
                    }

                    var runner = await db.Runners.Include(x => x.Lifecycle).Include(x => x.Job).FirstOrDefaultAsync(x => x.Hostname == cspServer.Name);

                    if (runner == null)
                    {
                        _logger.LogWarning($"CSP server {cspServer.Name} not found in database - skipping");
                        continue;
                    }

                    // CATEGORY 4A: Protection for >24h jobs
                    // Runners can vanish from GitHub registration after 24h but still be processing
                    if (runner.Job != null)
                    {
                        try
                        {
                            var targetConfig = Program.Config.TargetConfigs.FirstOrDefault(x => x.Name == runner.Job.Owner);
                            if (targetConfig != null)
                            {
                                var githubJob = await GitHubApi.GetJobInfoForOrg(runner.Job.GithubJobId, runner.Job.Repository, targetConfig.GitHubToken);

                                if (githubJob?.Status == "running" || githubJob?.Status == "in_progress")
                                {
                                    _logger.LogWarning($"[>24H JOB] Runner {cspServer.Name} not in GitHub but job {runner.Job.JobId} still running - protecting");
                                    continue;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"Unable to check GitHub job status for {runner.Hostname}: {ex.Message}");
                            SentrySdk.CaptureException(ex, scope => scope.Level = SentryLevel.Warning);
                        }
                    }

                    // Check if already queued for deletion
                    if (!SafeToQueueDeletion(runner))
                    {
                        int queueCount = runner.Lifecycle.Count(x => x.Status == RunnerStatus.DeletionQueued);

                        if (queueCount > 10)
                        {
                            // Force retry after 10 attempts
                            _logger.LogWarning($"Runner {runner.Hostname} stuck after {queueCount} deletion attempts - forcing retry");
                            runner.Lifecycle.Add(new()
                            {
                                Status = RunnerStatus.DeletionQueued,
                                Event = $"Forcing retry after {queueCount} failed deletion attempts",
                                EventTimeUtc = DateTime.UtcNow
                            });
                            await db.SaveChangesAsync();

                            _queues.DeleteTasks.Enqueue(new()
                            {
                                RunnerDbId = runner.RunnerId,
                                ServerId = cspServer.Id
                            });
                        }
                        continue;
                    }

                    // Determine if ready for cleanup based on age and state
                    var runnerAge = DateTime.UtcNow - await GetRunnerCreationTime(runner, cspServer, db);
                    bool shouldCleanup = false;
                    string reason = "";

                    if (runner.LastState >= RunnerStatus.Provisioned && runnerAge > TimeSpan.FromMinutes(30))
                    {
                        shouldCleanup = true;
                        reason = $"Provisioned but not in GitHub for {runnerAge.TotalMinutes:F1} min";
                    }
                    else if (runner.LastState != RunnerStatus.Processing && runnerAge > TimeSpan.FromMinutes(30))
                    {
                        shouldCleanup = true;
                        reason = $"Not in GitHub and not processing for {runnerAge.TotalMinutes:F1} min";
                    }

                    if (shouldCleanup)
                    {
                        _logger.LogInformation($"[CSP CLEANUP] Removing {cspServer.Name} from {cc.CloudIdentifier} - {reason}");

                        runner.IsOnline = false;
                        runner.Lifecycle.Add(new()
                        {
                            Status = RunnerStatus.DeletionQueued,
                            Event = $"CSP cleanup: {reason}",
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
                _logger.LogError($"Failed during CSP cleanup for {cc.CloudIdentifier}: {ex.Message}");
                SentrySdk.CaptureException(ex);
            }
        }

        // CATEGORY 5: Database Consistency - Mark runners offline if not in GitHub
        var onlineRunnersInDb = await db.Runners.Include(x => x.Lifecycle).Where(x => x.IsOnline).ToListAsync();

        foreach (var runner in onlineRunnersInDb)
        {
            // Protection: Leave young runners alone (1 hour)
            if (DateTime.UtcNow - await GetRunnerCreationTime(runner, null, db) < TimeSpan.FromHours(1))
            {
                continue;
            }

            // Skip if registered in GitHub
            if (registeredServerNames.Contains(runner.Hostname))
            {
                continue;
            }

            // Skip if already queued for deletion
            if (runner.LastState == RunnerStatus.DeletionQueued)
            {
                continue;
            }

            _logger.LogWarning($"[DB CONSISTENCY] Runner {runner.Hostname} marked online but not in GitHub - marking offline");

            runner.Lifecycle.Add(new()
            {
                Status = RunnerStatus.VanishedOnCloud,
                Event = "Database consistency check - not found in GitHub",
                EventTimeUtc = DateTime.UtcNow
            });
            runner.IsOnline = false;
        }

        await db.SaveChangesAsync();
    }

    private async Task<bool> DeleteRunner(DeleteRunnerTask rt)
    {
        using var activity = Program.OrchestratorActivitySource.StartActivity("runner.delete");
        activity?.SetTag("runner.db_id", rt.RunnerDbId);
        activity?.SetTag("runner.server_id", rt.ServerId);

        await using var db = new ActionsRunnerContext();
        var runner = await db.Runners.Include(x => x.Lifecycle).FirstOrDefaultAsync(x => x.RunnerId == rt.RunnerDbId);

        // Handle case where runner no longer exists in database
        if (runner == null)
        {
            _logger.LogWarning($"DeleteRunner: Runner {rt.RunnerDbId} not found in database - may have been already deleted");

            // Try to delete from CSP anyway using all available cloud controllers
            bool deletedFromCsp = false;
            foreach (var cc in _cc)
            {
                try
                {
                    await cc.DeleteRunner(rt.ServerId);
                    _logger.LogInformation($"Deleted orphaned CSP server {rt.ServerId} from {cc.CloudIdentifier}");
                    deletedFromCsp = true;
                    break;
                }
                catch (Exception ex)
                {
                    // Expected - server might not exist on this CSP
                    _logger.LogDebug($"Server {rt.ServerId} not found on {cc.CloudIdentifier}: {ex.Message}");
                }
            }

            if (!deletedFromCsp && rt.RetryCount < 3)
            {
                // Retry a few times in case it's a temporary issue
                rt.RetryCount += 1;
                _queues.DeleteTasks.Enqueue(rt);
                _logger.LogWarning($"Retrying deletion for orphaned server {rt.ServerId} (attempt {rt.RetryCount})");
            }

            return deletedFromCsp;
        }

        activity?.SetTag("runner.hostname", runner.Hostname);
        activity?.SetTag("runner.cloud", runner.Cloud);

        try
        {
            // Find the correct cloud controller
            ICloudController cc = _cc.FirstOrDefault(x => x.CloudIdentifier == runner.Cloud);
            if (cc == null)
            {
                _logger.LogError($"No Cloud controller found for runner {runner.Hostname} on cloud {runner.Cloud}");

                // Mark as failed in database
                runner.Lifecycle.Add(new RunnerLifecycle
                {
                    Status = RunnerStatus.Failure,
                    EventTimeUtc = DateTime.UtcNow,
                    Event = $"No cloud controller found for cloud '{runner.Cloud}'"
                });
                await db.SaveChangesAsync();

                return false;
            }

            // Delete from CSP
            await cc.DeleteRunner(rt.ServerId);

            // Update database
            runner.IsOnline = false;
            runner.Lifecycle.Add(new()
            {
                Status = RunnerStatus.Deleted,
                EventTimeUtc = DateTime.UtcNow,
                Event = "Runner was successfully deleted from CSP"
            });
            await db.SaveChangesAsync();

            _logger.LogInformation($"Successfully deleted runner {runner.Hostname} (ServerId: {rt.ServerId}) from {cc.CloudIdentifier}");
            return true;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            SentrySdk.CaptureException(ex, scope =>
            {
                scope.SetTag("server-id", rt.ServerId.ToString());
                scope.SetTag("runner-hostname", runner.Hostname);
                scope.SetTag("cloud", runner.Cloud);
            });

            _logger.LogError($"Unable to delete runner {runner.Hostname} [ServerId: {rt.ServerId} | Retry: {rt.RetryCount}]: {ex.Message}");

            // Retry logic
            rt.RetryCount += 1;
            if (rt.RetryCount < 3)
            {
                _queues.DeleteTasks.Enqueue(rt);
                runner.Lifecycle.Add(new RunnerLifecycle
                {
                    Status = RunnerStatus.Failure,
                    EventTimeUtc = DateTime.UtcNow,
                    Event = $"Deletion failed (retry {rt.RetryCount}): {ex.Message}"
                });
            }
            else
            {
                _logger.LogError($"Retries exceeded for runner {runner.Hostname} (ServerId: {rt.ServerId}). Giving up.");
                runner.Lifecycle.Add(new RunnerLifecycle
                {
                    Status = RunnerStatus.Failure,
                    EventTimeUtc = DateTime.UtcNow,
                    Event = $"Retries exceeded after {rt.RetryCount} attempts. Giving up. Error: {ex.Message}"
                });
            }

            await db.SaveChangesAsync();
            return false;
        }
    }

    private async Task<bool> CreateRunner(CreateRunnerTask rt)
    {
        using var activity = Program.OrchestratorActivitySource.StartActivity("runner.create");
        activity?.SetTag("runner.db_id", rt.RunnerDbId);
        activity?.SetTag("runner.repo", rt.RepoName);

        await using var db = new ActionsRunnerContext();
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
                    activity?.SetTag("runner.hostname", newRunner.Name);
                    activity?.SetTag("runner.cloud", cc.CloudIdentifier);
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
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
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