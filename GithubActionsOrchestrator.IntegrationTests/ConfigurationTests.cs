using GithubActionsOrchestrator.IntegrationTests.TestConfiguration;
using Xunit;

namespace GithubActionsOrchestrator.IntegrationTests;

public class ConfigurationTests
{
    [Fact]
    public void LoadFromAppSettings_ShouldLoadValidConfiguration()
    {
        // Act
        var config = TestConfigurationLoader.LoadFromAppSettings();

        // Assert
        Assert.NotNull(config);
        Assert.Equal("ghr-test", config.RunnerPrefix);
        Assert.Equal(20000, config.MinVmId);
        Assert.Equal("10.128.1.2", config.PveHost);
        Assert.Equal("ghrunners@pve", config.PveUsername);
        Assert.NotEmpty(config.PvePassword);
        Assert.Equal(170, config.PveTemplate);
        Assert.Equal("https://example.com/scripts", config.ProvisionScriptBaseUrl);
        Assert.Equal("test-user", config.MetricUser);
        Assert.Equal("test-password", config.MetricPassword);
        Assert.Equal("http://localhost:5000", config.ControllerUrl);
        Assert.Equal("2.0.0", config.GithubAgentVersion);
        Assert.NotEmpty(config.DbConnectionString);
        
        // Assert sizes are loaded
        Assert.NotNull(config.Sizes);
        Assert.Equal(2, config.Sizes.Count);
        Assert.Contains(config.Sizes, s => s.Name == "small" && s.Arch == "x64");
        Assert.Contains(config.Sizes, s => s.Name == "medium" && s.Arch == "x64");
        
        // Assert profiles are loaded
        Assert.NotNull(config.Profiles);
        Assert.Equal(2, config.Profiles.Count);
        Assert.Contains(config.Profiles, p => p.Name == "default");
        Assert.Contains(config.Profiles, p => p.Name == "test-profile");
    }

    [Fact]
    public void LoadFromAppSettings_ShouldValidateProductionPrefix()
    {
        // This test verifies that the configuration validates against production settings
        // The current appsettings.test.json should pass validation since it uses "ghr-test"
        
        // Act & Assert - Should not throw
        var exception = Record.Exception(() => TestConfigurationLoader.LoadFromAppSettings());
        Assert.Null(exception);
    }
}