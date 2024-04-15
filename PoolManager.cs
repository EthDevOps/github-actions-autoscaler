namespace GithubActionsOrchestrator;

public class PoolManager : IHostedService, IDisposable
{
    private readonly CloudController _cc;
    private readonly ILogger<PoolManager> _logger;

    public PoolManager(ILogger<PoolManager> logger, CloudController cc)
    {
        _logger = logger;
        _cc = cc;
    }
    
    public void Dispose()
    {
        // TODO release managed resources here
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting base load runners...");

        var orgConfig = Program.Config.OrgConfigs;

        // 
        
        // Get active runner list
        foreach (var org in orgConfig)
        {
            _logger.LogInformation($"Starting for {org.OrgName}");
            
            string runnerToken = await Program.GetRunnerToken(org.GitHubToken, org.OrgName);
            List<Machine> existingRunners = _cc.GetRunnersForOrg(org.OrgName);
            
            foreach (var pool in org.Pools)
            {
                int existCt = existingRunners.Count(x => x.Size == pool.Size);
                int missingCt = pool.NumRunners - existCt;

                string arch = Program.Config.Sizes.FirstOrDefault(x => x.Name == pool.Size).Arch;
                
                for (int i = 0; i < missingCt; i++)
                {
                    // Create VM 
                    string name = await _cc.CreateNewRunner(arch, pool.Size, runnerToken, org.OrgName);
                    _logger.LogInformation($"[{i+1}/{missingCt}] Created runner for {org.OrgName}: {name}");
                }
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping all runners...");
        return Task.CompletedTask;
    }
}