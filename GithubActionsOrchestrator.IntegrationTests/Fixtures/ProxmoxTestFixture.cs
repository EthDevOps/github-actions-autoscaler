using GithubActionsOrchestrator.CloudControllers;
using GithubActionsOrchestrator.Database;
using GithubActionsOrchestrator.IntegrationTests.TestConfiguration;
using GithubActionsOrchestrator.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GithubActionsOrchestrator.IntegrationTests.Fixtures;

public class ProxmoxTestFixture : IDisposable
{
    private readonly List<long> _createdVmIds = new List<long>();
    private readonly ILogger<ProxmoxCloudController> _logger;

    public ProxmoxCloudController Controller { get; }
    public AutoScalerConfiguration TestConfig { get; }

    public ProxmoxTestFixture()
    {
        // Validate environment
        ValidateTestEnvironment();

        // Setup logging
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        _logger = loggerFactory.CreateLogger<ProxmoxCloudController>();

        // Create test configuration
        TestConfig = TestAutoScalerConfiguration.CreateTestConfiguration();

        // Set the Program.Config for the controller to use
        Program.Config = TestConfig;

        // Initialize test database synchronously (xUnit doesn't support async constructors)
        InitializeDatabaseAsync().GetAwaiter().GetResult();

        // Create controller with test configuration
        Controller = new ProxmoxCloudController(
            _logger,
            TestConfig.Sizes,
            TestConfig.ProvisionScriptBaseUrl,
            TestConfig.MetricUser,
            TestConfig.MetricPassword,
            TestConfig.PveHost,
            TestConfig.PveUsername,
            TestConfig.PvePassword,
            TestConfig.PveTemplate,
            TestConfig.MinVmId
        );
    }

    public void TrackCreatedVm(long vmId)
    {
        _createdVmIds.Add(vmId);
    }

    private async Task InitializeDatabaseAsync()
    {
        try
        {
            using var context = new ActionsRunnerContext();
            
            // Ensure the database exists and is up to date
            await context.Database.EnsureCreatedAsync();
            
            // Optionally run migrations if needed
            // await context.Database.MigrateAsync();
            
            Console.WriteLine("Test database initialized successfully");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to initialize test database: {ex.Message}. Make sure PostgreSQL test database is running on port 5433.", ex);
        }
    }

    private void ValidateTestEnvironment()
    {
        var requiredEnvVars = new[] { "PVE_HOST", "PVE_USERNAME", "PVE_PASSWORD" };
        var missingVars = requiredEnvVars.Where(var => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(var))).ToList();
        
        if (missingVars.Any())
        {
            throw new InvalidOperationException($"Missing required environment variables: {string.Join(", ", missingVars)}");
        }

        // Validate test settings to ensure we're not running against production
        var runnerPrefix = Environment.GetEnvironmentVariable("TEST_RUNNER_PREFIX") ?? "ghr-test";
        var minVmId = int.Parse(Environment.GetEnvironmentVariable("TEST_MIN_VM_ID") ?? "20000");

        if (runnerPrefix == "ghr")
        {
            throw new InvalidOperationException("Cannot run integration tests with production runner prefix 'ghr'. Use 'ghr-test' or another test prefix.");
        }

        if (minVmId < 20000)
        {
            throw new InvalidOperationException($"Test MinVmId ({minVmId}) must be >= 20000 to avoid production VM ID range collision.");
        }
    }

    public async Task CleanupCreatedVmsAsync()
    {
        foreach (var vmId in _createdVmIds)
        {
            try
            {
                await Controller.DeleteRunner(vmId);
                Console.WriteLine($"Cleaned up test VM: {vmId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to cleanup VM {vmId}: {ex.Message}");
            }
        }
        _createdVmIds.Clear();
    }

    public async Task<List<CspServer>> GetTestVmsAsync()
    {
        var allServers = await Controller.GetAllServersFromCsp();
        return allServers.Where(s => s.Name.StartsWith(TestConfig.RunnerPrefix)).ToList();
    }

    public void Dispose()
    {
        try
        {
            CleanupCreatedVmsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during fixture cleanup: {ex.Message}");
        }
    }
}