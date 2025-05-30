using GithubActionsOrchestrator.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql.Internal;

namespace GithubActionsOrchestrator;
[Route("/api")]
public class ApiController : Controller
{
    [Route("get-runners")]
    public async Task<IResult> GetRunners([FromQuery] int limit = 100, [FromQuery] int offset = 0)
    {
        var db = new ActionsRunnerContext();
        var recentRunners = await db.Runners.Include(x => x.Lifecycle).Include(x => x.Job).OrderByDescending(x => x.RunnerId).Skip(offset).Take(limit).ToListAsync();
        return Results.Json(recentRunners);
    }
    
    [Route("get-jobs")]
    public async Task<IResult> GetJobs([FromQuery] int limit = 100, [FromQuery] int offset = 0)
    {
        var db = new ActionsRunnerContext();
        var recentRunners = await db.Jobs.OrderByDescending(x => x.JobId).Skip(offset).Take(limit).ToListAsync();
        return Results.Json(recentRunners);
    }
    
    [Route("get-runner/{runnerid}")]
    public async Task<IResult> GetRunner(int runnerid)
    {
        var db = new ActionsRunnerContext();
        var runner = await db.Runners.Include(x => x.Lifecycle).FirstOrDefaultAsync(x => x.RunnerId == runnerid);
        return Results.Json(runner);
    }

    [Route("get-job/{jobid}")]
    public async Task<IResult> GetJob(int jobid)
    {
        var db = new ActionsRunnerContext();
        var job = await db.Jobs.FirstOrDefaultAsync(x => x.JobId == jobid);
        return Results.Json(job);
        
    }

    [Route("get-potential-runners/{jobId}")]
    public async Task<IResult> GetPotentialRunners(int jobId)
    {
        var db = new ActionsRunnerContext();
        var job = await db.Jobs.FirstOrDefaultAsync(x => x.JobId == jobId);
        
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
    public async Task<IResult> GetProvisionScript(string provisionId,[FromHeader(Name = "X-API-KEY")] string apiKey)
    {
        /*// Check if API key is provided
        if (string.IsNullOrEmpty(apiKey))
        {
            return Results.Unauthorized();
        }
    
        // Validate API key (replace with your actual validation logic)
        if (apiKey != Program.Config.ApiKey)
        {
            return Results.Unauthorized();
        }*/
 
        var db = new ActionsRunnerContext();
        var runner = await db.Runners.Where(x => x.ProvisionId.ToLower() == provisionId).FirstOrDefaultAsync();
        if(runner == null)
            return Results.NotFound();
        
        return Results.Content(runner.ProvisionPayload);

    }

    
}