using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Prometheus;
using Serilog;
using Serilog.Events;

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
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
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
        JsonSerializerOptions options = new()
        {
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            }
        };
        Config = JsonSerializer.Deserialize<AutoScalerConfiguration>(configJson, options) ??
                 throw new Exception("Unable to parse configuration");

        if (string.IsNullOrWhiteSpace(Config.HetznerToken))
        {
            Log.Error($"Hetzner cloud token not set in {configPath}");
            return;
        }

        Log.Information($"Loaded {Config.TargetConfigs.Count} targets and {Config.Sizes.Count} sizes.");

        // Prepare metrics
        using KestrelMetricServer server = new(port: 9000);
        server.Start();
        Log.Information("Metrics server listening on port 9000");

        // Init count metrics
        foreach (GithubTargetConfiguration org in Config.TargetConfigs)
        {
            foreach (MachineSize ms in Config.Sizes)
            {
                TotalMachineTime.Labels(org.Name, ms.Name).IncTo(0);
                PickedJobCount.Labels(org.Name, ms.Name).IncTo(0);
                QueuedJobCount.Labels(org.Name, ms.Name).IncTo(0);
                ProcessedJobCount.Labels(org.Name, ms.Name).IncTo(0);
            }
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSerilog();
        builder.Services.AddSingleton<RunnerQueue>();
        builder.Services.AddHostedService<PoolManager>();

        // Add services to the container.
        builder.Services.AddAuthorization();

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        builder.Services.AddSingleton(svc =>
        {
            ILogger<CloudController> logger = svc.GetRequiredService<ILogger<CloudController>>();
            return new CloudController(logger, Config.HetznerToken, persistPath, Config.Sizes,
                Config.ProvisionScriptBaseUrl, Config.MetricUser, Config.MetricPassword);
        });

        WebApplication app = builder.Build();

        app.MapPost("/add-runner", async (HttpRequest request,
            [FromServices] CloudController cloud, [FromServices] ILogger<Program> logger, [FromServices] RunnerQueue runnerQueue,[FromServices] RunnerQueue poolMgr) =>
        {
            // Read webhook from github
            JsonDocument json = await request.ReadFromJsonAsync<JsonDocument>();

            string repoNameRequest = json.RootElement.GetProperty("repo").GetString();
            string orgNameRequest = json.RootElement.GetProperty("org").GetString();
            List<string> labels = json.RootElement.GetProperty("labels").EnumerateArray()
                .Select(x => x.GetString()).ToList();

            // Needed to get properly cased names
            string orgName = Config.TargetConfigs.FirstOrDefault(x =>
                x.Target == TargetType.Organization && x.Name.ToLower() == orgNameRequest.ToLower())?.Name ?? orgNameRequest;
            string repoName = Config.TargetConfigs.FirstOrDefault(x =>
                x.Target == TargetType.Repository && x.Name.ToLower() == repoNameRequest.ToLower())?.Name ?? repoNameRequest;
            
            
            // Check if its an org or a repo
            if (String.IsNullOrEmpty(orgName))
            {
                logger.LogWarning($"Unable to retrieve organization. aborting.");
                return Results.StatusCode(400);
            }

            // Check if Org is configured
            bool isOrg = Config.TargetConfigs.Any(x => x.Name == orgName && x.Target == TargetType.Organization);
            bool isRepo = Config.TargetConfigs.Any(x => x.Name == repoName && x.Target == TargetType.Repository);
            
            if (!isOrg && !isRepo)
            {
                logger.LogWarning($"GitHub org {orgName} nor repo {repoName} is configured. Ignoring.");
                return Results.StatusCode(400);
            }

            try
            {
                await JobQueued(logger, repoName, labels, orgName, poolMgr, isRepo ? TargetType.Repository : TargetType.Organization);
            }
            catch (Exception ex)
            {
                // This should make the webhook as bad and the timer will redeliver it after a while
                Log.Error($"Failed to process manual trigger: {ex.Message}");
                return Results.StatusCode(500);
            }

            // All was well 
            return Results.StatusCode(201);
            
        });

        app.MapGet("/debug", (HttpRequest request,
            [FromServices] CloudController cloud, [FromServices] ILogger<Program> logger, [FromServices] RunnerQueue runnerQueue) =>
        {
            return Results.Json(cloud.GetAllRunners());
        });
        
        
        app.MapPost("/runner-state", async (HttpRequest request, 
            [FromServices] CloudController cloud, [FromServices] ILogger<Program> logger, [FromServices] RunnerQueue runnerQueue, [FromQuery] string hostname, [FromQuery] string state) =>
        {
            switch (state)
            {
                case "ok":
                    // Remove from provisioning dict
                    Log.Information($"Runner {hostname} finished provisioning.");
                    runnerQueue.CreatedRunners.Remove(hostname, out _);
                    break;
                case "error":
                    Log.Warning($"Runner {hostname} failed provisioning.");
                    if (request.Form.Files.Count > 0)
                    {
                        // Read the log file into a string
                        using var reader = new StreamReader(request.Form.Files[0].OpenReadStream());
                        string fileContent = await reader.ReadToEndAsync();
                        Log.Information($"LOGS FROM {hostname}\n\n{fileContent}");    
                    }
                    
                    // Get runner specs
                    Machine runner = cloud.GetRunnerByHostname(hostname);
                    
                    // Queue creation of a new runner
                    if (!runnerQueue.CreatedRunners.Remove(hostname, out CreateRunnerTask runnerSpec))
                    {
                       logger.LogError($"Unable to get previous settings for {hostname}");
                    }
                    
                    logger.LogInformation($"Re-creating runner of type [{runnerSpec.Size}, {runnerSpec.Arch}] for {runnerSpec.RepoName}");
                    runnerQueue.CreateTasks.Enqueue(runnerSpec);
                    
                    // Queue deletion of the failed runner
                    runnerQueue.DeleteTasks.Enqueue(new DeleteRunnerTask
                    {
                        ServerId = runner.Id 
                    });
                    break;
            }

            return Results.StatusCode(201);
        });
        
        // Prepare pools
        app.MapPost("/github-webhook", async (HttpRequest request, [FromServices] CloudController cloud, [FromServices] ILogger<Program> logger, [FromServices] RunnerQueue poolMgr) =>
        {
            // Verify webhook HMAC - TODO

            // Read webhook from github
            JsonDocument json = await request.ReadFromJsonAsync<JsonDocument>();

            string action;
            if (json.RootElement.TryGetProperty("action", out JsonElement actionJson))
            {
                action = actionJson.GetString() ?? string.Empty;
            }
            else
            {
                logger.LogDebug("No Action found. rejecting.");
                return Results.StatusCode(201);
            }

            if (!json.RootElement.TryGetProperty("workflow_job", out JsonElement workflowJson))
            {
                logger.LogDebug("Received a non-workflowJob request. Ignoring.");
                return Results.StatusCode(201);
            }

            List<string> labels = workflowJson.GetProperty("labels").EnumerateArray()
                .Select(x => x.GetString()).ToList();

            bool isSelfHosted = labels.Any(x => x.StartsWith("self-hosted-ghr"));

            if (!isSelfHosted)
            {
                logger.LogDebug($"Received a non self-hosted request. Ignoring. Labels: {string.Join('|', labels)}");
                return Results.StatusCode(201);
            }

            long jobId = workflowJson.GetProperty("id").GetInt64();
            string repoNameRequest = json.RootElement.GetProperty("repository").GetProperty("full_name").GetString();
            string orgNameRequest = json.RootElement.GetProperty("organization").GetProperty("login").GetString();

            // Needed to get properly cased names
            string orgName = Config.TargetConfigs.FirstOrDefault(x =>
                x.Target == TargetType.Organization && x.Name.ToLower() == orgNameRequest.ToLower())?.Name ?? orgNameRequest;
            string repoName = Config.TargetConfigs.FirstOrDefault(x =>
                x.Target == TargetType.Repository && x.Name.ToLower() == repoNameRequest.ToLower())?.Name ?? repoNameRequest;
            
            
            // Check if its an org or a repo
            if (String.IsNullOrEmpty(orgName))
            {
                logger.LogWarning($"Unable to retrieve organization. aborting.");
                return Results.StatusCode(201);
            }

            // Check if Org is configured
            bool isOrg = Config.TargetConfigs.Any(x => x.Name == orgName && x.Target == TargetType.Organization);
            bool isRepo = Config.TargetConfigs.Any(x => x.Name == repoName && x.Target == TargetType.Repository);
            
            if (!isOrg && !isRepo)
            {
                logger.LogWarning($"GitHub org {orgName} nor repo {repoName} is configured. Ignoring.");
                return Results.StatusCode(201);
            }

            try
            {
                switch (action)
                {
                    case "queued":
                        await JobQueued(logger, repoName, labels, orgName, poolMgr, isRepo ? TargetType.Repository : TargetType.Organization);
                        break;
                    case "in_progress":
                        JobInProgress(workflowJson, logger, jobId, cloud, repoName, orgName);
                        break;
                    case "completed":
                        JobCompleted(logger, jobId, cloud, poolMgr, repoName);
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

    private static void JobCompleted(ILogger<Program> logger, long jobId, CloudController cloud, RunnerQueue poolMgr, string repoName)
    {
        logger.LogInformation(
            $"Workflow Job {jobId} in repo {repoName} has completed. Queuing deletion of VM associated with Job.");
        Machine vm = cloud.GetInfoForJob(jobId);
        if (vm == null)
        {
            logger.LogError($"No VM on record for JobID: {jobId}");
        }
        else
        {
            poolMgr.DeleteTasks.Enqueue(new DeleteRunnerTask
            {
                ServerId = vm.Id,
            });
            ProcessedJobCount.Labels(vm.TargetName, vm.Size).Inc();

            double secondsAlive = (DateTime.UtcNow - vm.CreatedAt).TotalSeconds;
            TotalMachineTime.Labels(vm.TargetName, vm.Size).Inc(secondsAlive);
        }
    }

    private static void JobInProgress(JsonElement workflowJson, ILogger<Program> logger, long jobId, CloudController cloud,
        string repoName, string orgName)
    {
        string runnerName = workflowJson.GetProperty("runner_name").GetString();
        string jobUrl = workflowJson.GetProperty("url").GetString();
        logger.LogInformation($"Workflow Job {jobId} in repo {repoName} now in progress on {runnerName}");
        cloud.AddJobClaimToRunner(runnerName, jobId, jobUrl, repoName);

        string jobSize = cloud.GetInfoForJob(jobId)?.Size;
        if (jobSize != null) PickedJobCount.Labels(orgName, jobSize).Inc();
    }

    private static async Task JobQueued(ILogger<Program> logger, string repoName, List<string> labels, string orgName, RunnerQueue poolMgr, TargetType targetType)
    {
        logger.LogInformation(
            $"New Workflow Job was queued for {repoName}. Queuing VM creation to replenish pool...");
        
        string size = string.Empty;
        string arch = string.Empty;
        string profileName = string.Empty;
        bool isCustom = false;

        // Check if this is a custom run
        if (labels.Contains("self-hosted-ghr-custom"))
        {
            isCustom = true;
            // Check for a profile label
            string profileLabel = labels.FirstOrDefault(x => x.StartsWith("profile-"));
            if (string.IsNullOrEmpty(profileLabel))
            {
                logger.LogError("No profile label given for custom mode. Ignoring.");
                return;
            }

            profileName = profileLabel.Split('-')[1];

        }
        foreach (MachineSize csize in Config.Sizes)
        {
            if (labels.Contains(csize.Name) || labels.Contains($"self-hosted-ghr-{csize.Name}"))
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
        string githubToken = targetType switch
        {
            TargetType.Organization => Config.TargetConfigs.FirstOrDefault(x => x.Name == orgName && x.Target == TargetType.Organization)?.GitHubToken,
            TargetType.Repository => Config.TargetConfigs.FirstOrDefault(x => x.Name == repoName && x.Target == TargetType.Repository)?.GitHubToken,
            _ => throw new ArgumentOutOfRangeException(nameof(targetType), targetType, null)
        }; 
       
        if (String.IsNullOrEmpty(githubToken))
        {
            switch (targetType)
            {
                case TargetType.Organization:
                    logger.LogError($"Unknown organization: {orgName} - check setup. aborting.");
                    break;
                case TargetType.Repository:
                    logger.LogError($"Unknown repository: {repoName} - check setup. aborting.");
                    break;
            }

            return;
        }

        string runnerToken = targetType switch
        {
            TargetType.Organization => await GitHubApi.GetRunnerTokenForOrg(githubToken, orgName),
            TargetType.Repository => await GitHubApi.GetRunnerTokenForRepo(githubToken, repoName),
            _ => throw new ArgumentOutOfRangeException(nameof(targetType), targetType, null)
        };
            
        if (String.IsNullOrEmpty(runnerToken))
        {
            logger.LogError("Unable to get new runner token. aborting.");
            return;
        }

        
        
        poolMgr.CreateTasks.Enqueue(new CreateRunnerTask
        {
            Arch = arch,
            Size = size,
            RunnerToken = runnerToken,
            OrgName = orgName,
            RepoName = repoName,
            IsCustom = isCustom,
            ProfileName = profileName,
            TargetType = targetType,
            
        });

        QueuedJobCount.Labels(orgName, size).Inc();
    }
}