using GithubActionsOrchestrator.Database;
using GithubActionsOrchestrator.Models;
using GithubActionsOrchestrator.CloudControllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql.Internal;

namespace GithubActionsOrchestrator;
[Route("/api")]
public class ApiController : Controller
{
    private readonly RunnerQueue _runnerQueue;
    private readonly ILogger<ApiController> _logger;

    public ApiController(RunnerQueue runnerQueue, ILogger<ApiController> logger)
    {
        _runnerQueue = runnerQueue;
        _logger = logger;
    }
    [Route("get-runners")]
    public async Task<IResult> GetRunners([FromQuery] int limit = 100, [FromQuery] int offset = 0)
    {
        await using var db = new ActionsRunnerContext();
        var recentRunners = await db.Runners.Include(x => x.Lifecycle).Include(x => x.Job).OrderByDescending(x => x.RunnerId).Skip(offset).Take(limit).ToListAsync();
        return Results.Json(recentRunners);
    }
    
    [Route("get-jobs")]
    public async Task<IResult> GetJobs([FromQuery] int limit = 100, [FromQuery] int offset = 0)
    {
        await using var db = new ActionsRunnerContext();
        var recentRunners = await db.Jobs.OrderByDescending(x => x.JobId).Skip(offset).Take(limit).ToListAsync();
        return Results.Json(recentRunners);
    }
    
    [Route("get-runner/{runnerid}")]
    public async Task<IResult> GetRunner(int runnerid)
    {
        await using var db = new ActionsRunnerContext();
        var runner = await db.Runners.Include(x => x.Lifecycle).FirstOrDefaultAsync(x => x.RunnerId == runnerid);
        return Results.Json(runner);
    }

    [Route("get-job/{jobid}")]
    public async Task<IResult> GetJob(int jobid)
    {
        await using var db = new ActionsRunnerContext();
        var job = await db.Jobs.FirstOrDefaultAsync(x => x.JobId == jobid);
        return Results.Json(job);
        
    }

    [Route("get-potential-runners/{jobId}")]
    public async Task<IResult> GetPotentialRunners(int jobId)
    {
        await using var db = new ActionsRunnerContext();
        var job = await db.Jobs.FirstOrDefaultAsync(x => x.JobId == jobId);

        if (job == null)
            return Results.NotFound(new { message = $"Job {jobId} not found" });

        // get labels
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
    
    [Route("provision/{provisionId}")]
    public async Task<IResult> GetProvisionScript(string provisionId,[FromHeader(Name = "X-API-KEY")] string apiKey, [FromServices] IServiceProvider serviceProvider)
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
        if(runner == null)
            return Results.NotFound();

        
        return Results.Content(runner.ProvisionPayload);

    }

    [Route("kill-non-processing-runners")]
    [HttpPost]
    public async Task<IResult> KillNonProcessingRunners()
    {
        await using var db = new ActionsRunnerContext();

        // Find all runners that are not in Processing state using the lifecycle table
        var excludedStatuses = new[] { RunnerStatus.Processing, RunnerStatus.DeletionQueued, RunnerStatus.Deleted };
        var runnersToKill = await db.Runners
            .AsNoTracking()
            .Where(r => db.RunnerLifecycles
                .Where(rl => rl.RunnerId == r.RunnerId)
                .Any() &&
                !excludedStatuses.Contains(
                    db.RunnerLifecycles
                        .Where(rl => rl.RunnerId == r.RunnerId)
                        .OrderByDescending(rl => rl.EventTimeUtc)
                        .Select(rl => rl.Status)
                        .FirstOrDefault()))
            .Select(r => new { r.RunnerId, r.CloudServerId, r.Hostname })
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
            message = $"Queued {runnersToKill.Count} runners for deletion",
            killedRunners = runnersToKill.Select(r => new { r.RunnerId, r.Hostname }).ToList()
        });
    }

    [Route("health")]
    [HttpGet]
    public IResult Health()
    {
        return Results.Ok(new { status = "healthy" });
    }
}