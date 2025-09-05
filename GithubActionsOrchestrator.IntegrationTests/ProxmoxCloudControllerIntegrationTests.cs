using GithubActionsOrchestrator.IntegrationTests.Fixtures;
using GithubActionsOrchestrator.Models;
using Xunit;

namespace GithubActionsOrchestrator.IntegrationTests;

[Collection("Proxmox Integration Tests")]
public class ProxmoxCloudControllerIntegrationTests : IClassFixture<ProxmoxTestFixture>
{
    private readonly ProxmoxTestFixture _fixture;

    public ProxmoxCloudControllerIntegrationTests(ProxmoxTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetAllServersFromCsp_ShouldReturnOnlyTestVms()
    {
        // Act
        var servers = await _fixture.Controller.GetAllServersFromCsp();

        // Assert
        Assert.All(servers, server =>
        {
            Assert.StartsWith(_fixture.TestConfig.RunnerPrefix, server.Name);
        });
    }

    [Fact]
    public async Task GetServerCountFromCsp_ShouldReturnCorrectCount()
    {
        // Act
        var count = await _fixture.Controller.GetServerCountFromCsp();
        var servers = await _fixture.Controller.GetAllServersFromCsp();

        // Assert
        Assert.Equal(servers.Count, count);
    }

    [Fact]
    public async Task CreateNewRunner_ShouldCreateVmWithCorrectProperties()
    {
        // Arrange
        const string arch = "x64";
        const string size = "small";
        const string runnerToken = "test-token-123";
        const string targetName = "test-org";

        // Act
        var machine = await _fixture.Controller.CreateNewRunner(arch, size, runnerToken, targetName);

        // Assert - Track for cleanup
        _fixture.TrackCreatedVm(machine.Id);

        // Verify machine properties
        Assert.NotEqual(0, machine.Id);
        Assert.True(machine.Id >= _fixture.TestConfig.MinVmId, $"VM ID {machine.Id} should be >= {_fixture.TestConfig.MinVmId}");
        Assert.True(machine.Id < _fixture.TestConfig.MinVmId + 10000, $"VM ID {machine.Id} should be < {_fixture.TestConfig.MinVmId + 10000}");
        Assert.StartsWith(_fixture.TestConfig.RunnerPrefix, machine.Name);
        Assert.Equal(targetName, machine.TargetName);
        Assert.Equal(size, machine.Size);
        Assert.Equal(arch, machine.Arch);
        Assert.Equal("default", machine.Profile);
        Assert.False(machine.IsCustom);
        Assert.NotNull(machine.ProvisionPayload);
        Assert.Contains(runnerToken, machine.ProvisionPayload);
        Assert.Contains(_fixture.TestConfig.RunnerPrefix, machine.ProvisionPayload);
    }

    [Fact]
    public async Task CreateNewRunner_WithCustomProfile_ShouldSetCorrectProperties()
    {
        // Arrange
        const string arch = "x64";
        const string size = "medium";
        const string runnerToken = "test-token-456";
        const string targetName = "test-org-custom";
        const string profileName = "test-profile";

        // Act
        var machine = await _fixture.Controller.CreateNewRunner(arch, size, runnerToken, targetName, true, profileName);

        // Assert - Track for cleanup
        _fixture.TrackCreatedVm(machine.Id);

        // Verify custom properties
        Assert.True(machine.IsCustom);
        Assert.Equal(profileName, machine.Profile);
        Assert.Contains("GH_IS_CUSTOM='1'", machine.ProvisionPayload);
    }

    [Fact]
    public async Task CreateMultipleRunners_ShouldGetDifferentVmIds()
    {
        // Arrange
        const string arch = "x64";
        const string size = "small";
        const string runnerToken = "test-token-789";
        const string targetName = "test-org-multi";

        // Act
        var machine1 = await _fixture.Controller.CreateNewRunner(arch, size, runnerToken, targetName);
        var machine2 = await _fixture.Controller.CreateNewRunner(arch, size, runnerToken, targetName);

        // Assert - Track for cleanup
        _fixture.TrackCreatedVm(machine1.Id);
        _fixture.TrackCreatedVm(machine2.Id);

        // Verify different VM IDs
        Assert.NotEqual(machine1.Id, machine2.Id);
        Assert.True(machine1.Id >= _fixture.TestConfig.MinVmId);
        Assert.True(machine2.Id >= _fixture.TestConfig.MinVmId);
        Assert.StartsWith(_fixture.TestConfig.RunnerPrefix, machine1.Name);
        Assert.StartsWith(_fixture.TestConfig.RunnerPrefix, machine2.Name);
        Assert.NotEqual(machine1.Name, machine2.Name);
    }

    [Fact]
    public async Task DeleteRunner_ShouldRemoveVmFromProxmox()
    {
        // Arrange - Create a VM first
        const string arch = "x64";
        const string size = "small";
        const string runnerToken = "test-token-delete";
        const string targetName = "test-org-delete";

        var machine = await _fixture.Controller.CreateNewRunner(arch, size, runnerToken, targetName);
        var vmId = machine.Id;

        // Verify VM was created
        var serversBeforeDelete = await _fixture.GetTestVmsAsync();
        Assert.Contains(serversBeforeDelete, s => s.Id == vmId);

        // Act - Delete the VM
        await _fixture.Controller.DeleteRunner(vmId);

        // Assert - VM should be removed
        var serversAfterDelete = await _fixture.GetTestVmsAsync();
        Assert.DoesNotContain(serversAfterDelete, s => s.Id == vmId);
    }

    [Fact]
    public async Task CreateNewRunner_ShouldGenerateValidProvisionPayload()
    {
        // Arrange
        const string arch = "x64";
        const string size = "small";
        const string runnerToken = "test-token-payload";
        const string targetName = "test-org-payload";

        // Act
        var machine = await _fixture.Controller.CreateNewRunner(arch, size, runnerToken, targetName);

        // Assert - Track for cleanup
        _fixture.TrackCreatedVm(machine.Id);

        // Verify provision payload contains expected values
        var payload = machine.ProvisionPayload;
        Assert.NotNull(payload);
        Assert.Contains($"export ORG_NAME='{targetName}'", payload);
        Assert.Contains($"export GH_TOKEN='{runnerToken}'", payload);
        Assert.Contains($"export RUNNER_SIZE='{size}'", payload);
        Assert.Contains($"export RUNNER_PREFIX='{_fixture.TestConfig.RunnerPrefix}'", payload);
        Assert.Contains($"export CONTROLLER_URL='{_fixture.TestConfig.ControllerUrl}'", payload);
        Assert.Contains($"export GH_PROFILE_NAME='default'", payload);
        Assert.Contains($"export GH_IS_CUSTOM='0'", payload);
        Assert.Contains("curl -fsSL", payload);
        Assert.Contains("bash /data/provision.sh", payload);
    }

    [Fact]
    public void CloudIdentifier_ShouldReturnPve()
    {
        // Act & Assert
        Assert.Equal("pve", _fixture.Controller.CloudIdentifier);
    }

    [Fact]
    public async Task CreateNewRunner_WithInvalidMachineType_ShouldThrowException()
    {
        // Arrange
        const string invalidArch = "invalid-arch";
        const string invalidSize = "invalid-size";
        const string runnerToken = "test-token-invalid";
        const string targetName = "test-org-invalid";

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(async () =>
        {
            await _fixture.Controller.CreateNewRunner(invalidArch, invalidSize, runnerToken, targetName);
        });
    }

    [Fact]
    public async Task UpdateRunnerIpAddressAsync_ShouldRetrieveActualIpAddress()
    {
        // Arrange - Create a VM first
        const string arch = "x64";
        const string size = "small";
        const string runnerToken = "test-token-ip";
        const string targetName = "test-org-ip";

        var machine = await _fixture.Controller.CreateNewRunner(arch, size, runnerToken, targetName);
        _fixture.TrackCreatedVm(machine.Id);

        // Verify initial IP is dummy
        Assert.Equal("0.0.0.0/0", machine.Ipv4);

        // Wait a bit for VM to fully start and get network configuration
        await Task.Delay(30000); // 30 seconds to allow VM to boot and get IP

        // Act - Update the IP address
        var actualIpAddress = await _fixture.Controller.UpdateRunnerIpAddressAsync(machine.Id);

        // Assert - IP should be updated (not dummy)
        Assert.NotEqual("0.0.0.0/0", actualIpAddress);
        Assert.NotNull(actualIpAddress);
        Assert.NotEmpty(actualIpAddress);
        
        // Basic IP format validation
        var parts = actualIpAddress.Split('.');
        Assert.Equal(4, parts.Length);
        Assert.All(parts, part => Assert.True(int.TryParse(part, out var value) && value >= 0 && value <= 255));
    }

    [Fact]
    public async Task UpdateRunnerIpAddressAsync_WithNonExistentVm_ShouldReturnDummyIp()
    {
        // Arrange - Use a non-existent VM ID
        const long nonExistentVmId = 99999;

        // Act
        var ipAddress = await _fixture.Controller.UpdateRunnerIpAddressAsync(nonExistentVmId);

        // Assert - Should return dummy IP for non-existent VM
        Assert.Equal("0.0.0.0/0", ipAddress);
    }
}