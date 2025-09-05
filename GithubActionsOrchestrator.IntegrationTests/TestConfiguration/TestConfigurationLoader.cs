using GithubActionsOrchestrator.Models;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace GithubActionsOrchestrator.IntegrationTests.TestConfiguration;

public static class TestConfigurationLoader
{
    public static AutoScalerConfiguration LoadFromAppSettings()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.test.json", optional: false, reloadOnChange: false);

        var configuration = builder.Build();
        var testConfig = new AutoScalerConfiguration();

        // Bind the TestConfiguration section to our configuration object
        configuration.GetSection("TestConfiguration").Bind(testConfig);

        // Validate essential settings
        ValidateTestConfiguration(testConfig);

        return testConfig;
    }

    private static void ValidateTestConfiguration(AutoScalerConfiguration config)
    {
        if (string.IsNullOrEmpty(config.RunnerPrefix))
            throw new InvalidOperationException("RunnerPrefix is required in test configuration");

        if (config.RunnerPrefix == "ghr")
            throw new InvalidOperationException("Cannot run integration tests with production runner prefix 'ghr'. Use 'ghr-test' or another test prefix.");

        if (config.MinVmId < 20000)
            throw new InvalidOperationException($"Test MinVmId ({config.MinVmId}) must be >= 20000 to avoid production VM ID range collision.");

        if (string.IsNullOrEmpty(config.PveHost))
            throw new InvalidOperationException("PveHost is required in test configuration");

        if (string.IsNullOrEmpty(config.PveUsername))
            throw new InvalidOperationException("PveUsername is required in test configuration");

        if (string.IsNullOrEmpty(config.PvePassword))
            throw new InvalidOperationException("PvePassword is required in test configuration");

        if (config.Sizes == null || !config.Sizes.Any())
            throw new InvalidOperationException("At least one machine size must be defined in test configuration");

        if (config.Profiles == null || !config.Profiles.Any())
            throw new InvalidOperationException("At least one profile must be defined in test configuration");
    }
}