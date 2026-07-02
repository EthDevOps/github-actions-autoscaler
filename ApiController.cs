using GithubActionsOrchestrator.Database;
using GithubActionsOrchestrator.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GithubActionsOrchestrator;

[Route("/api")]
public class ApiController : Controller
{
    private const int StuckJobThresholdMinutes = 10;

    private readonly RunnerQueue _runnerQueue;
    private readonly ILogger<ApiController> _logger;

    public ApiController(RunnerQueue runnerQueue, ILogger<ApiController> logger)
    {
        _runnerQueue = runnerQueue;
        _logger = logger;
    }

    // ---------------------------------------------------------------------
    // Lists (server-side filtering, sorting and pagination)
    // ---------------------------------------------------------------------

    [Route("get-runners")]
    [HttpGet]
    public async Task<IResult> GetRunners(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        [FromQuery] string? search = null,
        [FromQuery] string? cloud = null,
        [FromQuery] string? owner = null,
        [FromQuery] string? size = null,
        [FromQuery] string? profile = null,
        [FromQuery] int? status = null,
        [FromQuery] bool? online = null,
        [FromQuery] string? sortField = null,
        [FromQuery] int sortOrder = -1)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);

        await using var db = new ActionsRunnerContext();

        IQueryable<Runner> query = db.Runners
            .Include(x => x.Lifecycle)
            .Include(x => x.Job);

        if (!string.IsNullOrWhiteSpace(search))
        {
            string s = search.Trim();
            query = query.Where(r => r.Hostname.Contains(s) || r.IPv4.Contains(s) || r.Owner.Contains(s));
        }

        if (!string.IsNullOrWhiteSpace(cloud))
            query = query.Where(r => r.Cloud == cloud);
        if (!string.IsNullOrWhiteSpace(owner))
            query = query.Where(r => r.Owner == owner);
        if (!string.IsNullOrWhiteSpace(size))
            query = query.Where(r => r.Size == size);
        if (!string.IsNullOrWhiteSpace(profile))
            query = query.Where(r => r.Profile == profile);
        if (online.HasValue)
            query = query.Where(r => r.IsOnline == online.Value);

        // LastState is computed from the lifecycle, so filter via a correlated subquery.
        if (status.HasValue)
        {
            var wanted = (RunnerStatus)status.Value;
            query = query.Where(r => db.RunnerLifecycles
                .Where(rl => rl.RunnerId == r.RunnerId)
                .OrderByDescending(rl => rl.EventTimeUtc)
                .Select(rl => rl.Status)
                .FirstOrDefault() == wanted);
        }

        int total = await query.CountAsync();

        bool desc = sortOrder < 0;
        query = (sortField?.ToLowerInvariant()) switch
        {
            "hostname" => desc ? query.OrderByDescending(r => r.Hostname) : query.OrderBy(r => r.Hostname),
            "owner" => desc ? query.OrderByDescending(r => r.Owner) : query.OrderBy(r => r.Owner),
            "size" => desc ? query.OrderByDescending(r => r.Size) : query.OrderBy(r => r.Size),
            "cloud" => desc ? query.OrderByDescending(r => r.Cloud) : query.OrderBy(r => r.Cloud),
            "profile" => desc ? query.OrderByDescending(r => r.Profile) : query.OrderBy(r => r.Profile),
            "isonline" => desc ? query.OrderByDescending(r => r.IsOnline) : query.OrderBy(r => r.IsOnline),
            _ => desc ? query.OrderByDescending(r => r.RunnerId) : query.OrderBy(r => r.RunnerId),
        };

        var items = await query.Skip(offset).Take(limit).ToListAsync();
        return Results.Json(new { items, total });
    }

    [Route("get-jobs")]
    [HttpGet]
    public async Task<IResult> GetJobs(
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        [FromQuery] string? search = null,
        [FromQuery] string? owner = null,
        [FromQuery] string? repository = null,
        [FromQuery] string? size = null,
        [FromQuery] string? profile = null,
        [FromQuery] int? state = null,
        [FromQuery] bool stuckOnly = false,
        [FromQuery] string? sortField = null,
        [FromQuery] int sortOrder = -1)
    {
        limit = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);

        await using var db = new ActionsRunnerContext();

        IQueryable<Job> query = db.Jobs;

        if (!string.IsNullOrWhiteSpace(search))
        {
            string s = search.Trim();
            query = query.Where(j => j.Repository.Contains(s) || j.Owner.Contains(s) || j.GithubJobId.ToString().Contains(s));
        }

        if (!string.IsNullOrWhiteSpace(owner))
            query = query.Where(j => j.Owner == owner);
        if (!string.IsNullOrWhiteSpace(repository))
            query = query.Where(j => j.Repository == repository);
        if (!string.IsNullOrWhiteSpace(size))
            query = query.Where(j => j.RequestedSize == size);
        if (!string.IsNullOrWhiteSpace(profile))
            query = query.Where(j => j.RequestedProfile == profile);
        if (state.HasValue)
            query = query.Where(j => j.State == (JobState)state.Value);

        if (stuckOnly)
        {
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-StuckJobThresholdMinutes);
            query = query.Where(j =>
                (j.State == JobState.Queued || j.State == JobState.Throttled) && j.QueueTime < cutoff);
        }

        int total = await query.CountAsync();

        bool desc = sortOrder < 0;
        query = (sortField?.ToLowerInvariant()) switch
        {
            "owner" => desc ? query.OrderByDescending(j => j.Owner) : query.OrderBy(j => j.Owner),
            "repository" => desc ? query.OrderByDescending(j => j.Repository) : query.OrderBy(j => j.Repository),
            "state" => desc ? query.OrderByDescending(j => j.State) : query.OrderBy(j => j.State),
            "queuetime" => desc ? query.OrderByDescending(j => j.QueueTime) : query.OrderBy(j => j.QueueTime),
            "inprogresstime" => desc ? query.OrderByDescending(j => j.InProgressTime) : query.OrderBy(j => j.InProgressTime),
            "completetime" => desc ? query.OrderByDescending(j => j.CompleteTime) : query.OrderBy(j => j.CompleteTime),
            _ => desc ? query.OrderByDescending(j => j.JobId) : query.OrderBy(j => j.JobId),
        };

        var items = await query.Skip(offset).Take(limit).ToListAsync();
        return Results.Json(new { items, total });
    }

    [Route("get-stuck-jobs")]
    [HttpGet]
    public async Task<IResult> GetStuckJobs()
    {
        await using var db = new ActionsRunnerContext();
        DateTime cutoff = DateTime.UtcNow.AddMinutes(-StuckJobThresholdMinutes);

        var jobs = await db.Jobs
            .Where(j => (j.State == JobState.Queued || j.State == JobState.Throttled) && j.QueueTime < cutoff)
            .OrderBy(j => j.QueueTime)
            .ToListAsync();

        return Results.Json(new { items = jobs, total = jobs.Count, thresholdMinutes = StuckJobThresholdMinutes });
    }

    [Route("get-runner/{runnerid}")]
    [HttpGet]
    public async Task<IResult> GetRunner(int runnerid)
    {
        await using var db = new ActionsRunnerContext();
        var runner = await db.Runners.Include(x => x.Lifecycle).Include(x => x.Job).FirstOrDefaultAsync(x => x.RunnerId == runnerid);
        return runner == null ? Results.NotFound() : Results.Json(runner);
    }

    [Route("get-job/{jobid}")]
    [HttpGet]
    public async Task<IResult> GetJob(int jobid)
    {
        await using var db = new ActionsRunnerContext();
        var job = await db.Jobs.FirstOrDefaultAsync(x => x.JobId == jobid);
        return job == null ? Results.NotFound() : Results.Json(job);
    }

    [Route("get-potential-runners/{jobId}")]
    [HttpGet]
    public async Task<IResult> GetPotentialRunners(int jobId)
    {
        await using var db = new ActionsRunnerContext();
        var job = await db.Jobs.FirstOrDefaultAsync(x => x.JobId == jobId);

        if (job == null)
            return Results.NotFound(new { message = $"Job {jobId} not found" });

        string size = job.RequestedSize;
        string owner = job.Owner;
        string profile = job.RequestedProfile;

        var potentialRunners = db.Runners
            .Include(x => x.Lifecycle)
            .Where(x => x.Size == size && x.Owner == owner && x.Profile == profile)
            .AsEnumerable()
            .Where(x => x.LastState == RunnerStatus.Created || x.LastState == RunnerStatus.Provisioned)
            .ToList();

        return Results.Json(potentialRunners);
    }

    // ---------------------------------------------------------------------
    // Dashboard stats & filter options
    // ---------------------------------------------------------------------

    [Route("stats")]
    [HttpGet]
    public async Task<IResult> GetStats()
    {
        await using var db = new ActionsRunnerContext();
        DateTime stuckCutoff = DateTime.UtcNow.AddMinutes(-StuckJobThresholdMinutes);

        var jobStateGroups = await db.Jobs
            .GroupBy(j => j.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync();

        int stuckJobs = await db.Jobs.CountAsync(j =>
            (j.State == JobState.Queued || j.State == JobState.Throttled) && j.QueueTime < stuckCutoff);
        int throttledJobs = await db.Jobs.CountAsync(j => j.State == JobState.Throttled);

        int onlineRunners = await db.Runners.CountAsync(r => r.IsOnline);

        // LastState is computed from the lifecycle. Fetch the latest status per runner and group in memory.
        var runnerStates = await db.Runners
            .Select(r => db.RunnerLifecycles
                .Where(rl => rl.RunnerId == r.RunnerId)
                .OrderByDescending(rl => rl.EventTimeUtc)
                .Select(rl => rl.Status)
                .FirstOrDefault())
            .ToListAsync();

        var runnersByState = runnerStates
            .GroupBy(s => s)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        int activeRunners = runnerStates.Count(s =>
            s == RunnerStatus.Created || s == RunnerStatus.Provisioned || s == RunnerStatus.Processing);

        var runnersByCloud = await db.Runners
            .GroupBy(r => r.Cloud)
            .Select(g => new { Cloud = g.Key, Count = g.Count() })
            .ToListAsync();

        return Results.Json(new
        {
            jobs = new
            {
                total = jobStateGroups.Sum(g => g.Count),
                byState = jobStateGroups.ToDictionary(g => g.State.ToString(), g => g.Count),
                stuck = stuckJobs,
                throttled = throttledJobs,
            },
            runners = new
            {
                total = runnerStates.Count,
                online = onlineRunners,
                active = activeRunners,
                byState = runnersByState,
                byCloud = runnersByCloud.ToDictionary(g => g.Cloud ?? "unknown", g => g.Count),
            },
            queues = new
            {
                create = _runnerQueue.CreateTasks.Count,
                delete = _runnerQueue.DeleteTasks.Count,
                provisioning = _runnerQueue.CreatedRunners.Count,
            },
            generatedAt = DateTime.UtcNow,
        });
    }

    [Route("filter-options")]
    [HttpGet]
    public async Task<IResult> GetFilterOptions()
    {
        await using var db = new ActionsRunnerContext();

        var owners = await db.Runners.Select(r => r.Owner).Distinct().OrderBy(x => x).ToListAsync();
        var sizes = await db.Runners.Select(r => r.Size).Distinct().OrderBy(x => x).ToListAsync();
        var profiles = await db.Runners.Select(r => r.Profile).Distinct().OrderBy(x => x).ToListAsync();
        var clouds = await db.Runners.Select(r => r.Cloud).Distinct().OrderBy(x => x).ToListAsync();
        var repositories = await db.Jobs.Select(j => j.Repository).Distinct().OrderBy(x => x).ToListAsync();

        return Results.Json(new
        {
            owners = owners.Where(x => !string.IsNullOrEmpty(x)),
            sizes = sizes.Where(x => !string.IsNullOrEmpty(x)),
            profiles = profiles.Where(x => !string.IsNullOrEmpty(x)),
            clouds = clouds.Where(x => !string.IsNullOrEmpty(x)),
            repositories = repositories.Where(x => !string.IsNullOrEmpty(x)),
            configuredTargets = Program.Config.TargetConfigs?
                .Select(t => new { name = t.Name, target = t.Target.ToString() }),
        });
    }

    // ---------------------------------------------------------------------
    // Actions
    // ---------------------------------------------------------------------

    [Route("runners/{runnerId}/delete")]
    [HttpPost]
    public async Task<IResult> DeleteRunner(int runnerId)
    {
        await using var db = new ActionsRunnerContext();
        var runner = await db.Runners.Include(x => x.Lifecycle).FirstOrDefaultAsync(x => x.RunnerId == runnerId);
        if (runner == null)
            return Results.NotFound(new { message = $"Runner {runnerId} not found" });

        if (runner.LastState is RunnerStatus.DeletionQueued or RunnerStatus.Deleted)
            return Results.Json(new { message = $"Runner {runnerId} is already {runner.LastState}." });

        runner.Lifecycle.Add(new RunnerLifecycle
        {
            EventTimeUtc = DateTime.UtcNow,
            Status = RunnerStatus.DeletionQueued,
            Event = "Deletion requested via dashboard API"
        });
        await db.SaveChangesAsync();

        _runnerQueue.DeleteTasks.Enqueue(new DeleteRunnerTask
        {
            ServerId = runner.CloudServerId,
            RunnerDbId = runner.RunnerId
        });

        _logger.LogWarning($"Runner {runnerId} ({runner.Hostname}) queued for deletion via dashboard API");
        return Results.Json(new { message = $"Runner {runnerId} ({runner.Hostname}) queued for deletion." });
    }

    public record CreateRunnerRequest(string Owner, string Size, string? Profile, string? Repository);

    [Route("runners")]
    [HttpPost]
    public async Task<IResult> CreateRunner([FromBody] CreateRunnerRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Owner) || string.IsNullOrWhiteSpace(request.Size))
            return Results.BadRequest(new { message = "owner and size are required" });

        var target = Program.Config.TargetConfigs?
            .FirstOrDefault(x => string.Equals(x.Name, request.Owner, StringComparison.OrdinalIgnoreCase));
        if (target == null)
            return Results.BadRequest(new { message = $"Owner '{request.Owner}' is not a configured target." });

        if (Program.Config.Sizes != null && Program.Config.Sizes.All(s => s.Name != request.Size))
            return Results.BadRequest(new { message = $"Size '{request.Size}' is not a configured size." });

        string profile = string.IsNullOrWhiteSpace(request.Profile) ? "default" : request.Profile;
        string repoName = string.IsNullOrWhiteSpace(request.Repository) ? target.Name : request.Repository;
        string arch = Program.Config.Sizes?.FirstOrDefault(x => x.Name == request.Size)?.Arch;

        int runnerId = await CreateRunnerInternal(target.Name, request.Size, profile, arch, target.Target, repoName,
            isStuckReplacement: false, stuckJobId: null, reason: "Created manually via dashboard API");

        _logger.LogWarning($"Runner {runnerId} ({request.Size}/{profile} for {target.Name}) queued for creation via dashboard API");
        return Results.Json(new
        {
            message = $"Runner queued for creation ({request.Size}/{profile} for {target.Name}).",
            runnerId
        });
    }

    [Route("jobs/{jobId}/reschedule")]
    [HttpPost]
    public async Task<IResult> RescheduleJob(int jobId)
    {
        await using var db = new ActionsRunnerContext();
        var job = await db.Jobs.FirstOrDefaultAsync(x => x.JobId == jobId);
        if (job == null)
            return Results.NotFound(new { message = $"Job {jobId} not found" });

        if (job.State is not (JobState.Queued or JobState.Throttled))
            return Results.BadRequest(new { message = $"Job {jobId} is {job.State}; only Queued/Throttled jobs can be rescheduled." });

        var target = Program.Config.TargetConfigs?
            .FirstOrDefault(x => string.Equals(x.Name, job.Owner, StringComparison.OrdinalIgnoreCase));
        if (target == null)
            return Results.BadRequest(new { message = $"Job owner '{job.Owner}' is not a configured target." });

        // Don't double-schedule if a replacement is already queued for this job.
        if (_runnerQueue.CreateTasks.Any(x => x.IsStuckReplacement && x.StuckJobId == job.JobId))
            return Results.Json(new { message = $"A replacement runner is already queued for job {jobId}." });

        string profile = string.IsNullOrWhiteSpace(job.RequestedProfile) ? "default" : job.RequestedProfile;
        string arch = Program.Config.Sizes?.FirstOrDefault(x => x.Name == job.RequestedSize)?.Arch;

        int runnerId = await CreateRunnerInternal(job.Owner, job.RequestedSize, profile, arch, target.Target,
            job.Repository, isStuckReplacement: true, stuckJobId: job.JobId,
            reason: $"Rescheduled for job {job.JobId} via dashboard API");

        _logger.LogWarning($"Reschedule requested for job {jobId} ({job.Repository}); runner {runnerId} queued.");
        return Results.Json(new
        {
            message = $"Replacement runner queued for job {jobId} ({job.Repository}).",
            runnerId
        });
    }

    private async Task<int> CreateRunnerInternal(string owner, string size, string profile, string arch,
        GitHub.TargetType targetType, string repoName, bool isStuckReplacement, int? stuckJobId, string reason)
    {
        await using var db = new ActionsRunnerContext();

        Runner newRunner = new()
        {
            Size = size,
            Cloud = "htz",
            Hostname = "Unknown",
            Profile = profile,
            Lifecycle =
            [
                new RunnerLifecycle
                {
                    EventTimeUtc = DateTime.UtcNow,
                    Status = RunnerStatus.CreationQueued,
                    Event = reason
                }
            ],
            IsOnline = false,
            Arch = arch,
            IPv4 = string.Empty,
            IsCustom = profile != "default",
            Owner = owner,
            StuckJobReplacement = isStuckReplacement
        };
        await db.Runners.AddAsync(newRunner);
        await db.SaveChangesAsync();

        _runnerQueue.CreateTasks.Enqueue(new CreateRunnerTask
        {
            RepoName = repoName,
            TargetType = targetType,
            RunnerDbId = newRunner.RunnerId,
            IsStuckReplacement = isStuckReplacement,
            StuckJobId = stuckJobId
        });

        return newRunner.RunnerId;
    }

    [Route("provision/{provisionId}")]
    public async Task<IResult> GetProvisionScript(string provisionId, [FromHeader(Name = "X-API-KEY")] string apiKey, [FromServices] IServiceProvider serviceProvider)
    {
        // Check if API key is provided
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError("API key is missing for /api/provision");
            //return Results.Unauthorized();
        }

        // Validate API key
        if (apiKey != Program.Config.ApiKey)
        {
            _logger.LogError("API key is not matching config for /api/provision");
            //return Results.Unauthorized();
        }

        await using var db = new ActionsRunnerContext();
        var runner = await db.Runners.Where(x => x.ProvisionId.ToLower() == provisionId).FirstOrDefaultAsync();
        if (runner == null)
            return Results.NotFound();

        return Results.Content(runner.ProvisionPayload);
    }

    [Route("kill-non-processing-runners")]
    [HttpPost]
    public async Task<IResult> KillNonProcessingRunners([FromQuery] string cloud = null)
    {
        await using var db = new ActionsRunnerContext();

        // Find all runners that are not in Processing state using the lifecycle table.
        // Exclude runners with no cloud server (CloudServerId == 0) — nothing to delete on the CSP.
        var excludedStatuses = new[] { RunnerStatus.Processing, RunnerStatus.DeletionQueued, RunnerStatus.Deleted };
        var query = db.Runners
            .AsNoTracking()
            .Where(r => r.CloudServerId != 0);

        if (!string.IsNullOrEmpty(cloud))
        {
            query = query.Where(r => r.Cloud == cloud);
        }

        var runnersToKill = await query
            .Where(r => db.RunnerLifecycles
                .Where(rl => rl.RunnerId == r.RunnerId)
                .Any() &&
                !excludedStatuses.Contains(
                    db.RunnerLifecycles
                        .Where(rl => rl.RunnerId == r.RunnerId)
                        .OrderByDescending(rl => rl.EventTimeUtc)
                        .Select(rl => rl.Status)
                        .FirstOrDefault()))
            .Select(r => new { r.RunnerId, r.CloudServerId, r.Hostname, r.Cloud })
            .ToListAsync();

        // Queue deletion for each runner
        foreach (var runner in runnersToKill)
        {
            _runnerQueue.DeleteTasks.Enqueue(new DeleteRunnerTask
            {
                ServerId = runner.CloudServerId,
                RunnerDbId = runner.RunnerId
            });
        }

        return Results.Json(new
        {
            message = $"Queued {runnersToKill.Count} runners for deletion" + (cloud != null ? $" on cloud '{cloud}'" : ""),
            killedRunners = runnersToKill.Select(r => new { r.RunnerId, r.Hostname, r.Cloud }).ToList()
        });
    }

    [Route("clear-creation-queue")]
    [HttpPost]
    public async Task<IResult> ClearCreationQueue()
    {
        await using var db = new ActionsRunnerContext();
        var queuedTasks = await db.CreateTaskQueues.ToListAsync();
        int count = queuedTasks.Count;
        db.CreateTaskQueues.RemoveRange(queuedTasks);
        await db.SaveChangesAsync();

        _logger.LogWarning($"Creation queue cleared: removed {count} tasks");
        return Results.Json(new { message = $"Cleared {count} tasks from creation queue" });
    }

    [Route("health")]
    [HttpGet]
    public IResult Health()
    {
        return Results.Ok(new { status = "healthy" });
    }
}
