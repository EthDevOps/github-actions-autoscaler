using GithubActionsOrchestrator.Models;

namespace GithubActionsOrchestrator.IntegrationTests.TestConfiguration;

public class TestAutoScalerConfiguration
{
    public static AutoScalerConfiguration CreateTestConfiguration()
    {
        return new AutoScalerConfiguration
        {
            RunnerPrefix = GetEnvironmentVariable("TEST_RUNNER_PREFIX", "ghr-test"),
            MinVmId = int.Parse(GetEnvironmentVariable("TEST_MIN_VM_ID", "20000")),
            PveHost = GetRequiredEnvironmentVariable("PVE_HOST"),
            PveUsername = GetRequiredEnvironmentVariable("PVE_USERNAME"),
            PvePassword = GetRequiredEnvironmentVariable("PVE_PASSWORD"),
            PveTemplate = int.Parse(GetEnvironmentVariable("TEST_PVE_TEMPLATE", "100")),
            ProvisionScriptBaseUrl = GetEnvironmentVariable("TEST_PROVISION_URL", "https://example.com/scripts"),
            MetricUser = GetEnvironmentVariable("TEST_METRIC_USER", "test-user"),
            MetricPassword = GetEnvironmentVariable("TEST_METRIC_PASSWORD", "test-password"),
            ControllerUrl = GetEnvironmentVariable("TEST_CONTROLLER_URL", "http://localhost:5000"),
            GithubAgentVersion = GetEnvironmentVariable("TEST_GITHUB_AGENT_VERSION", "2.0.0"),
            DbConnectionString = GetEnvironmentVariable("TEST_DB_CONNECTION_STRING", "Host=localhost;Port=5433;Database=github_actions_orchestrator_test;Username=test_user;Password=test_password"),
            Sizes = CreateTestMachineSizes(),
            Profiles = CreateTestProfiles()
        };
    }

    private static List<MachineSize> CreateTestMachineSizes()
    {
        return new List<MachineSize>
        {
            new MachineSize
            {
                Name = "small",
                Arch = "x64",
                VmTypes = new List<MachineType>
                {
                    new MachineType
                    {
                        Cloud = "pve",
                        VmType = "2c2g",
                        Priority = 1
                    }
                }
            },
            new MachineSize
            {
                Name = "medium",
                Arch = "x64",
                VmTypes = new List<MachineType>
                {
                    new MachineType
                    {
                        Cloud = "pve",
                        VmType = "4c4g",
                        Priority = 1
                    }
                }
            }
        };
    }

    private static List<RunnerProfile> CreateTestProfiles()
    {
        return new List<RunnerProfile>
        {
            new RunnerProfile
            {
                Name = "default",
                ScriptName = "ubuntu",
                ScriptVersion = 1,
                OsImageName = "ubuntu-22.04",
                IsCustomImage = false,
                UsePrivateNetworks = false
            },
            new RunnerProfile
            {
                Name = "test-profile",
                ScriptName = "test",
                ScriptVersion = 1,
                OsImageName = "test-image",
                IsCustomImage = true,
                UsePrivateNetworks = false
            }
        };
    }

    private static string GetEnvironmentVariable(string name, string defaultValue)
    {
        return Environment.GetEnvironmentVariable(name) ?? defaultValue;
    }

    private static string GetRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"Required environment variable '{name}' is not set");
        }
        return value;
    }
}