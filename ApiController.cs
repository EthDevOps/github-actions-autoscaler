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

    [Route("get-job/{jobid}")]
    public async Task<IResult> GetJob(int jobid)
    {
        var db = new ActionsRunnerContext();
        var job = await db.Jobs.FirstOrDefaultAsync(x => x.JobId == jobid);
        return Results.Json(job);
        
    }
    
    
}