using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Prometheus;
using Serilog;

namespace GithubActionsOrchestrator;

public class Program
{
    public static AutoScalerConfiguration Config = new();

    private static readonly Counter ProcessedJobCount = Metrics
        .CreateCounter("github_autoscaler_jobs_processed", "Number of processed jobs", labelNames: ["org", "size"]);

    private static readonly Counter QueuedJobCount = Metrics
        .CreateCounter("github_autoscaler_jobs_queued", "Number of queued jobs", labelNames: ["org", "size"]);

    private static readonly Counter PickedJobCount = Metrics
        .CreateCounter("github_autoscaler_jobs_picked", "Number of jobs picked up by a runner",
            labelNames: ["org", "size"]);


    private static readonly Counter TotalMachineTime = Metrics
        .CreateCounter("github_total_machine_time", "Number of seconds machines were alive",
            labelNames: ["org", "size"]);

    public static void Main(string[] args)
    {
        // Set up logging
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();


        string persistPath = Environment.GetEnvironmentVariable("PERSIST_DIR") ??
                             Directory.CreateTempSubdirectory().FullName;
        string configDir = Environment.GetEnvironmentVariable("CONFIG_DIR") ??
                           Directory.CreateTempSubdirectory().FullName;

        // Setup pool config
        string configPath = Path.Combine(configDir, "config.json");
        if (!File.Exists(configPath))
        {
            Log.Error($"Unable to read config file at {configPath}");
            return;
        }

        string configJson = File.ReadAllText(configPath);
        Config = JsonSerializer.Deserialize<AutoScalerConfiguration>(configJson) ??
                 throw new Exception("Unable to parse configuration");

        if (string.IsNullOrWhiteSpace(Config.HetznerToken))
        {
            Log.Error($"Hetzner cloud token not set in {configPath}");
            return;
        }

        Log.Information($"Loaded {Config.OrgConfigs.Count} orgs and {Config.Sizes.Count} sizes.");

        // Prepare metrics
        using var server = new KestrelMetricServer(port: 9000);
        server.Start();
        Log.Information("Metrics server listening on port 9000");

        // Init count metrics
        foreach (var org in Config.OrgConfigs)
        {
            foreach (var ms in Config.Sizes)
            {
                TotalMachineTime.Labels(org.OrgName, ms.Name).IncTo(0);
                PickedJobCount.Labels(org.OrgName, ms.Name).IncTo(0);
                QueuedJobCount.Labels(org.OrgName, ms.Name).IncTo(0);
                ProcessedJobCount.Labels(org.OrgName, ms.Name).IncTo(0);
            }
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSerilog();
        builder.Services.AddHostedService<PoolManager>();

        // Add services to the container.
        builder.Services.AddAuthorization();

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        builder.Services.AddSingleton<CloudController>(svc =>
        {
            var logger = svc.GetRequiredService<ILogger<CloudController>>();
            return new CloudController(logger, Config.HetznerToken, persistPath, Config.Sizes,
                Config.ProvisionScriptBaseUrl, Config.MetricUser, Config.MetricPassword);
        });

        var app = builder.Build();

        // Prepare pools
        app.MapPost("/github-webhook", async (HttpRequest request, [FromServices] CloudController cloud,
            [FromServices] ILogger<Program> logger, [FromServices] PoolManager poolMgr) =>
        {
            logger.LogInformation("Received GitHub Webhook");
            // Verify webhook HMAC - TODO

            // Read webhook from github
            var json = await request.ReadFromJsonAsync<JsonDocument>();

            string action = String.Empty;
            if (json.RootElement.TryGetProperty("action", out JsonElement actionJson))
            {
                action = actionJson.GetString() ?? string.Empty;
            }
            else
            {
                logger.LogWarning("No Action found. rejecting.");
                return Results.StatusCode(201);
            }

            if (!json.RootElement.TryGetProperty("workflow_job", out var workflowJson))
            {
                logger.LogWarning("Received a non-workflowJob request. Ignoring.");
                return Results.StatusCode(201);
            }

            List<string?> labels = workflowJson.GetProperty("labels").EnumerateArray()
                .Select(x => x.GetString()).ToList();

            bool isSelfHosted = labels.Any(x => x.StartsWith("self-hosted"));

            if (!isSelfHosted)
            {
                logger.LogInformation(
                    $"Received a non self-hosted request. Ignoring. Labels: {string.Join('|', labels)}");
                return Results.StatusCode(201);
            }

            long jobId = workflowJson.GetProperty("id").GetInt64();
            string repoName = json.RootElement.GetProperty("repository").GetProperty("full_name").GetString();
            string orgNameReq = json.RootElement.GetProperty("organization").GetProperty("login").GetString();

            string orgName = Config.OrgConfigs.FirstOrDefault(x => x.OrgName.ToLower() == orgNameReq.ToLower())
                ?.OrgName;
            if (String.IsNullOrEmpty(orgName))
            {
                logger.LogError($"Unknown organization: {orgName} - check setup. aborting.");
                return Results.StatusCode(201);
            }

            // Check if Org is configured
            if (Config.OrgConfigs.All(x => x.OrgName != orgName))
            {
                logger.LogWarning($"GitHub Org {orgName} is not configured. Ignoring.");
                return Results.StatusCode(201);
            }

            try
            {
                switch (action)
                {
                    case "queued":
                        await JobQueued(logger, repoName, labels, orgName, poolMgr);
                        break;
                    case "in_progress":
                        JobInProgress(workflowJson, logger, jobId, cloud, repoName, orgName);
                        break;
                    case "completed":
                        JobCompleted(logger, jobId, cloud, poolMgr);
                        break;
                    default:
                        logger.LogWarning("Unknown action. Ignoring");
                        break;
                }
            }
            catch (Exception ex)
            {
                // This should make the webhook as bad and the timer will redeliver it after a while
                Log.Error($"Failed to process {action} webhook: {ex.Message}");
                return Results.StatusCode(500);
            }
            
            // All was well 
            return Results.StatusCode(201);
        });

        app.Run();
    }

    private static void JobCompleted(ILogger<Program> logger, long jobId, CloudController cloud, PoolManager poolMgr)
    {
        logger.LogInformation(
            $"Workflow Job {jobId} has completed. Queuing deletion VM associated with Job...");
        var vm = cloud.GetInfoForJob(jobId);
        if (vm == null)
        {
            logger.LogError($"No VM on record for JobID: {jobId}");
        }
        else
        {
            poolMgr.Tasks.Enqueue(new RunnerTask
            {
                Action = RunnerAction.Delete,
                ServerId = vm.Id
            });
            ProcessedJobCount.Labels(vm.OrgName, vm.Size).Inc();

            double secondsAlive = (DateTime.UtcNow - vm.CreatedAt).TotalSeconds;
            TotalMachineTime.Labels(vm.OrgName, vm.Size).Inc(secondsAlive);
        }
    }

    private static void JobInProgress(JsonElement workflowJson, ILogger<Program> logger, long jobId, CloudController cloud,
        string? repoName, string orgName)
    {
        string? runnerName = workflowJson.GetProperty("runner_name").GetString();
        string? jobUrl = workflowJson.GetProperty("url").GetString();
        logger.LogInformation($"Workflow Job {jobId} now in progress on {runnerName}");
        cloud.AddJobClaimToRunner(runnerName, jobId, jobUrl, repoName);

        string jobSize = cloud.GetInfoForJob(jobId)?.Size;
        PickedJobCount.Labels(orgName, jobSize).Inc();
    }

    private static async Task JobQueued(ILogger<Program> logger, string? repoName, List<string?> labels, string orgName, PoolManager poolMgr)
    {
        logger.LogInformation(
            $"New Workflow Job was queued for {repoName}. Queuing VM creation to replenish pool...");

        string size = string.Empty;
        string arch = String.Empty;
        foreach (var csize in Config.Sizes)
        {
            if (labels.Contains(csize.Name) || labels.Contains($"self-hosted-{csize.Name}"))
            {
                size = csize.Name;
                arch = csize.Arch;
                break;
            }

            if (labels.Contains($"{csize.Name}-x64") || labels.Contains($"self-hosted-{csize.Name}-x64"))
            {
                size = csize.Name;
                arch = csize.Arch;
                break;
            }
        }

        if (string.IsNullOrEmpty(size))
        {
            logger.LogWarning($"No runner size specified for workflow in {repoName}. Ignoring.");
            return;
        }

        // Create a new runner
        string? githubToken = Config.OrgConfigs.FirstOrDefault(x => x.OrgName == orgName)?.GitHubToken;
        if (String.IsNullOrEmpty(githubToken))
        {
            logger.LogError($"Unknown organization: {orgName} - check setup. aborting.");
            return;
        }

        string runnerToken = await GitHubApi.GetRunnerToken(githubToken, orgName);
        if (String.IsNullOrEmpty(runnerToken))
        {
            logger.LogError("Unable to get new runner token. aborting.");
            return;
        }

        poolMgr.Tasks.Enqueue(new RunnerTask
        {
            Action = RunnerAction.Create,
            Arch = arch,
            Size = size,
            RunnerToken = runnerToken,
            OrgName = orgName
        });

        QueuedJobCount.Labels(orgName, size).Inc();

        return;
    }
}