using GithubActionsOrchestrator.Database;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GithubActionsOrchestrator;
[Route("/api")]
public class ApiController : Controller
{
    [Route("get-runners")]
    public async Task<IResult> GetRunners()
    {
        var db = new ActionsRunnerContext();
        var recentRunners = await db.Runners.Include(x => x.Lifecycle).OrderByDescending(x => x.RunnerId).Take(100).ToListAsync();
        return Results.Json(recentRunners);
    }
    
    [Route("get-jobs")]
    public async Task<IResult> GetJobs()
    {
        var db = new ActionsRunnerContext();
        var recentRunners = await db.Jobs.OrderByDescending(x => x.JobId).Take(100).ToListAsync();
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
    
    
}