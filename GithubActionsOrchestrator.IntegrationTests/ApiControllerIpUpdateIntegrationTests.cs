using GithubActionsOrchestrator.IntegrationTests.Fixtures;
using GithubActionsOrchestrator.Database;
using GithubActionsOrchestrator.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using GithubActionsOrchestrator.CloudControllers;

namespace GithubActionsOrchestrator.IntegrationTests;

[Collection("Proxmox Integration Tests")]
public class ApiControllerIpUpdateIntegrationTests : IClassFixture<ProxmoxTestFixture>
{
    private readonly ProxmoxTestFixture _fixture;

    public ApiControllerIpUpdateIntegrationTests(ProxmoxTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetProvisionScript_ShouldUpdateRunnerIpAddress_WhenCalledForProxmoxRunner()
    {
        // Arrange - Create a VM first
        const string arch = "x64";
        const string size = "small";
        const string runnerToken = "test-token-provision-ip";
        const string targetName = "test-org-provision";

        var machine = await _fixture.Controller.CreateNewRunner(arch, size, runnerToken, targetName);
        _fixture.TrackCreatedVm(machine.Id);

        // Create a runner record in the database
        using var db = new ActionsRunnerContext();
        var runner = new Runner
        {
            CloudServerId = machine.Id,
            Hostname = machine.Name,
            IPv4 = "0.0.0.0/0", // Initial dummy IP
            Cloud = "pve",
            Size = size,
            Profile = "default",
            Arch = arch,
            Owner = targetName,
            IsCustom = false,
            ProvisionId = machine.ProvisionId,
            ProvisionPayload = machine.ProvisionPayload
        };

        // Add initial lifecycle entry using navigation property
        runner.Lifecycle = new List<RunnerLifecycle>
        {
            new RunnerLifecycle
            {
                Status = RunnerStatus.Created,
                EventTimeUtc = DateTime.UtcNow,
                Event = "Test runner created"
            }
        };
        
        db.Runners.Add(runner);
        await db.SaveChangesAsync();

        // Setup service provider for ApiController
        var services = new ServiceCollection();
        services.AddSingleton<ProxmoxCloudController>(_fixture.Controller);
        services.AddSingleton<RunnerQueue>(new RunnerQueue()); // Mock queue
        var serviceProvider = services.BuildServiceProvider();

        var apiController = new ApiController(serviceProvider.GetService<RunnerQueue>());

        // Wait for VM to start and get network configuration
        await Task.Delay(30000); // 30 seconds

        // Act - Call GetProvisionScript which should update the IP
        var result = await apiController.GetProvisionScript(machine.ProvisionId.ToLower(), null, serviceProvider);

        // Assert - Check that the runner's IP was updated in the database
        // Refresh the entity from the database to get updated values
        await db.Entry(runner).ReloadAsync();
        
        Assert.NotEqual("0.0.0.0/0", runner.IPv4);
        Assert.NotNull(runner.IPv4);
        Assert.NotEmpty(runner.IPv4);
        
        // Basic IP format validation
        var parts = runner.IPv4.Split('.');
        Assert.Equal(4, parts.Length);
        Assert.All(parts, part => Assert.True(int.TryParse(part, out var value) && value >= 0 && value <= 255));

        // Verify the provision script is returned
        Assert.NotNull(result);
        
        // Cleanup - EF will cascade delete lifecycle records
        db.Runners.Remove(runner);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetProvisionScript_ShouldNotUpdateIp_WhenRunnerAlreadyHasValidIp()
    {
        // Arrange - Create a VM first
        const string arch = "x64";
        const string size = "small";
        const string runnerToken = "test-token-existing-ip";
        const string targetName = "test-org-existing";

        var machine = await _fixture.Controller.CreateNewRunner(arch, size, runnerToken, targetName);
        _fixture.TrackCreatedVm(machine.Id);

        // Create a runner record with existing valid IP
        const string existingIp = "192.168.1.100";
        using var db = new ActionsRunnerContext();
        var runner = new Runner
        {
            CloudServerId = machine.Id,
            Hostname = machine.Name,
            IPv4 = existingIp, // Already has valid IP
            Cloud = "pve",
            Size = size,
            Profile = "default",
            Arch = arch,
            Owner = targetName,
            IsCustom = false,
            ProvisionId = machine.ProvisionId,
            ProvisionPayload = machine.ProvisionPayload
        };

        db.Runners.Add(runner);
        await db.SaveChangesAsync();

        // Setup service provider for ApiController
        var services = new ServiceCollection();
        services.AddSingleton<ProxmoxCloudController>(_fixture.Controller);
        services.AddSingleton<RunnerQueue>(new RunnerQueue()); // Mock queue
        var serviceProvider = services.BuildServiceProvider();

        var apiController = new ApiController(serviceProvider.GetService<RunnerQueue>());

        // Act - Call GetProvisionScript
        var result = await apiController.GetProvisionScript(machine.ProvisionId.ToLower(), null, serviceProvider);

        // Assert - IP should remain unchanged
        using var dbCheck = new ActionsRunnerContext();
        var updatedRunner = await dbCheck.Runners.FirstOrDefaultAsync(x => x.RunnerId == runner.RunnerId);
        
        Assert.NotNull(updatedRunner);
        Assert.Equal(existingIp, updatedRunner.IPv4); // Should remain the same

        // Cleanup
        db.Runners.Remove(runner);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetProvisionScript_ShouldNotUpdateIp_WhenRunnerIsNotProxmox()
    {
        // Arrange - Create a runner record for non-Proxmox cloud
        using var db = new ActionsRunnerContext();
        var runner = new Runner
        {
            CloudServerId = 12345,
            Hostname = "test-hetzner-runner",
            IPv4 = "0.0.0.0/0", // Dummy IP
            Cloud = "hetzner", // Different cloud provider
            Size = "small",
            Profile = "default",
            Arch = "x64",
            Owner = "test-org",
            IsCustom = false,
            ProvisionId = "test-provision-id",
            ProvisionPayload = "echo 'test'"
        };

        db.Runners.Add(runner);
        await db.SaveChangesAsync();

        // Setup service provider for ApiController
        var services = new ServiceCollection();
        services.AddSingleton<ProxmoxCloudController>(_fixture.Controller);
        services.AddSingleton<RunnerQueue>(new RunnerQueue()); // Mock queue
        var serviceProvider = services.BuildServiceProvider();

        var apiController = new ApiController(serviceProvider.GetService<RunnerQueue>());

        // Act - Call GetProvisionScript
        var result = await apiController.GetProvisionScript(runner.ProvisionId.ToLower(), null, serviceProvider);

        // Assert - IP should remain unchanged for non-Proxmox runners
        using var dbCheck = new ActionsRunnerContext();
        var updatedRunner = await dbCheck.Runners.FirstOrDefaultAsync(x => x.RunnerId == runner.RunnerId);
        
        Assert.NotNull(updatedRunner);
        Assert.Equal("0.0.0.0/0", updatedRunner.IPv4); // Should remain dummy IP

        // Cleanup
        db.Runners.Remove(runner);
        await db.SaveChangesAsync();
    }
}