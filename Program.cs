using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GithubActionsOrchestrator.CloudControllers;
using GithubActionsOrchestrator.Database;
using GithubActionsOrchestrator.GitHub;
using GithubActionsOrchestrator.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    private static readonly Counter MachineFailedCount = Metrics
        .CreateCounter("github_autoscaler_machine_failed", "Number of machines failed to provision",
            labelNames: ["org", "size"]);
    
    private static readonly Counter MachineSuccessCount = Metrics
        .CreateCounter("github_autoscaler_machine_success", "Number of machines provisioned fine",
            labelNames: ["org", "size"]);
    
    private static readonly Counter TotalMachineTime = Metrics
        .CreateCounter("github_autoscaler_total_machine_time", "Number of seconds machines were alive",
            labelNames: ["org", "size"]);

    public static void Main(string[] args)
    {
        //Init GlitchTip
        SentrySdk.Init(options =>
        {
            options.Dsn = "https://32e67fe320454af79c685a59a3502116@glitchtip.ethquokkaops.io/1";
        });
        
        
        // Set up logging
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .CreateLogger();



        if (!LoadConfiguration())
        {
            Log.Error("Unable to do initial config load. Aborting.");
            return;
        }

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

        // Database migration
        Log.Information("Running Database migrations...");
        using (var db = new ActionsRunnerContext())
        {
            db.Database.Migrate();
        }
        Log.Information("Migrations complete.");


        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSerilog();
        builder.Services.AddSingleton<RunnerQueue>();
        builder.Services.AddHostedService<PoolManager>();

        // Add services to the container.
        

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowAll", builder =>
            {
                builder
                    .AllowAnyOrigin()    // Allow all origins
                    .AllowAnyMethod()    // Allow all HTTP methods
                    .AllowAnyHeader();   // Allow all headers
            });
        });
        builder.Services.AddControllers().AddJsonOptions(options =>
         {
             options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
         });

        builder.Services.AddSingleton<ICloudController, HetznerCloudController>(svc =>
        {
            ILogger<HetznerCloudController> logger = svc.GetRequiredService<ILogger<HetznerCloudController>>();
            return new HetznerCloudController(logger, Config.HetznerToken, Config.Sizes,
                Config.ProvisionScriptBaseUrl, Config.MetricUser, Config.MetricPassword);
        });
        
        builder.Services.AddSingleton<ICloudController, ProxmoxCloudController>(svc =>
        {
            ILogger<ProxmoxCloudController> logger = svc.GetRequiredService<ILogger<ProxmoxCloudController>>();
            return new ProxmoxCloudController(logger, Config.Sizes, Config.ProvisionScriptBaseUrl, Config.MetricUser, Config.MetricPassword, Config.PveHost, Config.PveUsername, Config.PvePassword, Config.PveTemplate, Config.MinVmId);
        });

        WebApplication app = builder.Build();
        app.UseCors("AllowAll");

        app.MapPost("/add-runner", AddRunnerManuallyHandler);
        app.MapPost("/runner-state", RunnerStateReportHandler);
        app.MapPost("/github-webhook", GithubWebhookHandler);
        app.MapControllers();
        foreach (var url in app.Urls)
        {
            Console.WriteLine($"Listening on {url}.");
        }

        Console.WriteLine("Start listening..."); 
        app.Run(Program.Config.ListenUrl);
    }

    static string GetFileHash(string filePath)
    {
        using SHA256 sha256 = SHA256.Create();
        // Open the file and compute the hash
        using FileStream stream = File.OpenRead(filePath);
        byte[] hashBytes = sha256.ComputeHash(stream);
        // Convert the byte array to a hexadecimal string
        StringBuilder hashString = new StringBuilder();
        foreach (byte b in hashBytes)
        {
            hashString.Append(b.ToString("x2")); // Convert each byte to a hexadecimal string
        }
        return hashString.ToString();
    }

    
    public static bool LoadConfiguration()
    {
        string configDir = Environment.GetEnvironmentVariable("CONFIG_DIR") ??
                           Directory.CreateTempSubdirectory().FullName;
        // Setup pool config
        string configPath = Path.Combine(configDir, "config.json");
        if (!File.Exists(configPath))
        {
            Log.Error($"Unable to locate config file at {configPath}");
            return false;
        }
        string configFileHash = GetFileHash(configPath);

        if (configFileHash == LoadedConfigHash)
        {
            return true;
        }
        
        Log.Information("Loading/refreshing configuration...");

        string configJson = File.ReadAllText(configPath);
        JsonSerializerOptions options = new()
        {
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            }
        };
        var loadedConfig = JsonSerializer.Deserialize<AutoScalerConfiguration>(configJson, options) ??
                 throw new Exception("Unable to parse configuration");

        if (string.IsNullOrWhiteSpace(loadedConfig.HetznerToken))
        {
            Log.Error($"Hetzner cloud token not set in {configPath}");
            return false;
        }
 
        Config = loadedConfig;
        LoadedConfigHash = configFileHash;
        Log.Information($"Loaded {Config.TargetConfigs.Count} targets and {Config.Sizes.Count} sizes.");
        return true;
    }

    public static string LoadedConfigHash { get; set; }

    private static async Task<IResult> GithubWebhookHandler(HttpRequest request, [FromServices] HetznerCloudController cloud, [FromServices] ILogger<Program> logger, [FromServices] RunnerQueue poolMgr)
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

        List<string> labels = workflowJson.GetProperty("labels")
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToList();

        bool isSelfHosted = labels.Any(x => x.StartsWith($"self-hosted-{Program.Config.RunnerPrefix}"));

        if (!isSelfHosted)
        {
            //logger.LogDebug($"Received a non self-hosted request. Ignoring. Labels: {string.Join('|', labels)}");
            return Results.StatusCode(201);
        }

        long jobId = workflowJson.GetProperty("id").GetInt64();
        string repoNameRequest = json.RootElement.GetProperty("repository").GetProperty("full_name").GetString();
        string orgNameRequest = json.RootElement.GetProperty("organization").GetProperty("login").GetString();
        string jobUrl = workflowJson.GetProperty("url").GetString();

        // Needed to get properly cased names
        string orgName = Config.TargetConfigs.FirstOrDefault(x => x.Target == TargetType.Organization && x.Name.ToLower() == orgNameRequest.ToLower())?.Name ?? orgNameRequest;
        string repoName = Config.TargetConfigs.FirstOrDefault(x => x.Target == TargetType.Repository && x.Name.ToLower() == repoNameRequest.ToLower())?.Name ?? repoNameRequest;


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

        
        var db = new ActionsRunnerContext();
        
        try
        {
            switch (action)
            {
                case "queued":
                    await JobQueued(logger, repoName, labels, orgName, poolMgr, isRepo ? TargetType.Repository : TargetType.Organization, jobId, jobUrl);
                    break;
                case "in_progress":
                    var dbWorkflow = await db.Jobs.FirstOrDefaultAsync(x => x.GithubJobId == jobId);
                    if (dbWorkflow == null)
                    {
                        logger.LogWarning("Processing job on manually created runner");
                        Job progressJob = new()
                        {
                            GithubJobId = jobId,
                            Repository = repoName,
                            Owner = isRepo ? repoName : orgName,
                            State = JobState.InProgress,
                            InProgressTime = DateTime.UtcNow,
                            JobUrl = jobUrl,
                            Orphan = true
                        };
                        await db.Jobs.AddAsync(progressJob);
                    }
                    else
                    {
                        dbWorkflow.State = JobState.InProgress;
                        dbWorkflow.QueueTime = DateTime.UtcNow;
                    }
                    await db.SaveChangesAsync();
                    await JobInProgress(workflowJson, logger, jobId, repoName, orgName);
                    break;
                case "completed":
                    var dbWorkflowComplete = await db.Jobs.FirstOrDefaultAsync(x => x.GithubJobId == jobId);
                    dbWorkflowComplete.State = JobState.Completed;
                    dbWorkflowComplete.CompleteTime = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    await JobCompleted(logger, jobId, poolMgr, repoName, orgName, workflowJson);
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
    }

    private static async Task<IResult> RunnerStateReportHandler(HttpRequest request, [FromServices] IServiceProvider serviceProvider , [FromServices] ILogger<Program> logger, [FromServices] RunnerQueue runnerQueue, [FromQuery] string hostname, [FromQuery] string state)
    {
        var db = new ActionsRunnerContext();
        var runner = await db.Runners.Include(x => x.Lifecycle).Include(x => x.Job).FirstOrDefaultAsync(x => x.Hostname == hostname);
        switch (state)
        {
            case "ok":
                // Remove from provisioning dict
                Log.Information($"Runner {hostname} finished provisioning.");
                MachineSuccessCount.Labels(runner.Arch, runner.Size).Inc();
                runnerQueue.CreatedRunners.Remove(hostname, out _);
                runner.Lifecycle.Add(new()
                {
                    Event = "Runner finished provisioning",
                    Status = RunnerStatus.Provisioned,
                    EventTimeUtc = DateTime.UtcNow
                });
                
                // Update the runner's IP address if using Proxmox and IP is still dummy
                if (runner.Cloud == "pve" && (string.IsNullOrEmpty(runner.IPv4) || runner.IPv4 == "0.0.0.0/0"))
                {
                    try
                    {
                        var cloudControllers = serviceProvider.GetServices<ICloudController>();
                        if (cloudControllers.FirstOrDefault(x => x.CloudIdentifier == "pve") is ProxmoxCloudController proxmoxController)
                        {
                            var actualIpAddress = await proxmoxController.UpdateRunnerIpAddressAsync(runner.CloudServerId);
                            if (actualIpAddress != "0.0.0.0/0")
                            {
                                runner.IPv4 = actualIpAddress;
                                await db.SaveChangesAsync();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log the error but don't fail the provision request
                        SentrySdk.CaptureException(ex);
                        Console.WriteLine($"Failed to update IP for runner {runner.RunnerId}: {ex.Message}");
                    }
                }
                
                
                await db.SaveChangesAsync();
                break;
            case "error":
                Log.Warning($"Runner {hostname} failed provisioning.");
                MachineFailedCount.Labels(runner.Arch, runner.Size).Inc();
                if (request.Form.Files.Count > 0)
                {
                    // Read the log file into a string
                    using var reader = new StreamReader(request.Form.Files[0].OpenReadStream());
                    string fileContent = await reader.ReadToEndAsync();
                    Log.Information($"LOGS FROM {hostname}\n\n{fileContent}");
                }

                runner.Lifecycle.Add(new()
                {
                    Event = "Runner failed provisioning",
                    Status = RunnerStatus.Failure,
                    EventTimeUtc = DateTime.UtcNow
                });
                // Get runner specs

                // Queue creation of a new runner
                if (!runnerQueue.CreatedRunners.Remove(hostname, out CreateRunnerTask runnerSpec))
                {
                    logger.LogError($"Unable to get previous settings for {hostname}");
                }
                else
                {
                    Runner newRunner = new()
                    {
                        Size = runner.Size,
                        Cloud = runner.Cloud,
                        Hostname = "Unknown",
                        Profile = runner.Profile,
                        Lifecycle =
                        [
                            new RunnerLifecycle
                            {
                                EventTimeUtc = DateTime.UtcNow,
                                Status = RunnerStatus.CreationQueued,
                                Event = $"Created as replacement for failed {hostname}"
                            }
                        ],
                        IsOnline = false,
                        Arch = runner.Arch,
                        IPv4 = string.Empty,
                        IsCustom = runner.IsCustom,
                        Owner = runner.Owner
                    };
                    await db.Runners.AddAsync(newRunner);
                    await db.SaveChangesAsync();

                    runnerSpec.RunnerDbId = newRunner.RunnerId;

                    logger.LogInformation($"Re-creating runner of type [{runner.Size}, {runner.Arch}] for {runner.Job.Repository}");
                    runnerQueue.CreateTasks.Enqueue(runnerSpec);

                    // Queue deletion of the failed runner
                    runnerQueue.DeleteTasks.Enqueue(new DeleteRunnerTask { ServerId = runner.CloudServerId });
                    await db.SaveChangesAsync();
                }

                break;
        }

        return Results.StatusCode(201);
    }

    private static async Task<IResult> AddRunnerManuallyHandler(HttpRequest request, [FromServices] HetznerCloudController cloud, [FromServices] ILogger<Program> logger, [FromServices] RunnerQueue runnerQueue, [FromServices] RunnerQueue poolMgr)
    {
        // Read webhook from github
        JsonDocument json = await request.ReadFromJsonAsync<JsonDocument>();

        string repoNameRequest = json.RootElement.GetProperty("repo").GetString();
        string orgNameRequest = json.RootElement.GetProperty("org").GetString();
        List<string> labels = json.RootElement.GetProperty("labels")
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToList();

        // Needed to get properly cased names
        string orgName = Config.TargetConfigs.FirstOrDefault(x => x.Target == TargetType.Organization && x.Name.ToLower() == orgNameRequest.ToLower())?.Name ?? orgNameRequest;
        string repoName = Config.TargetConfigs.FirstOrDefault(x => x.Target == TargetType.Repository && x.Name.ToLower() == repoNameRequest.ToLower())?.Name ?? repoNameRequest;


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
            await JobQueued(logger, repoName, labels, orgName, poolMgr, isRepo ? TargetType.Repository : TargetType.Organization, -1, null);
        }
        catch (Exception ex)
        {
            // This should make the webhook as bad and the timer will redeliver it after a while
            Log.Error($"Failed to process manual trigger: {ex.Message}");
            return Results.StatusCode(500);
        }

        // All was well 
        return Results.StatusCode(201);
    }

    private static async Task JobCompleted(ILogger<Program> logger, long jobId, RunnerQueue poolMgr, string repoName, string orgName, JsonElement workflowJson)
    {
        var db = new ActionsRunnerContext();
        var job = await db.Jobs
            .Include(job => job.Runner)
            .ThenInclude(runner => runner.Lifecycle)
            .FirstOrDefaultAsync(x => x.GithubJobId == jobId);

        if (job == null)
        {
            logger.LogWarning($"No job in database with ID: {jobId} on repo {repoName}");
            await db.Jobs.AddAsync(new Job
            {
                GithubJobId = jobId,
                Repository = repoName,
                Owner = orgName,
                State = JobState.Completed,
                Orphan = true,
                CompleteTime = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return;
        }
        
        logger.LogInformation(
            $"Workflow Job {jobId} in repo {repoName} has completed. Queuing deletion of VM associated with Job.");

        Runner jobRunner = null;
        if (job.Runner == null)
        {
            // Retroactivly assign runner to job
            string runnerName = workflowJson.GetProperty("runner_name").GetString();
            logger.LogError($"No VM on record for JobID: {jobId}. Trying to re-link to {runnerName}.");
            jobRunner = await db.LinkJobToRunner(jobId, runnerName);

            if (jobRunner == null)
            {
                logger.LogError("Unable to link runner. aborting");
                return;
            }
        }
        else
        {
            jobRunner = job.Runner;
        }
        
        // record event in DB
        if (jobRunner.Lifecycle.Any(x => x.Status == RunnerStatus.DeletionQueued))
        {
            jobRunner.Lifecycle.Add(new()
            {
                Status = RunnerStatus.DeletionQueued,
                EventTimeUtc = DateTime.UtcNow,
                Event = $"Workflow Job {jobId} in repo {repoName} has completed. Deletion already queued."
            });
            jobRunner.IsOnline = false;
            
        }
        else
        {
            jobRunner.Lifecycle.Add(new()
            {
                Status = RunnerStatus.DeletionQueued,
                EventTimeUtc = DateTime.UtcNow,
                Event = $"Workflow Job {jobId} in repo {repoName} has completed. Deletion queued."
            });
            jobRunner.IsOnline = false;
            // Sent to pool manager to delete
            poolMgr.DeleteTasks.Enqueue(new DeleteRunnerTask
            {
                ServerId = jobRunner.CloudServerId,
                RunnerDbId = jobRunner.RunnerId
            });
        }

        await db.SaveChangesAsync();
        
        ProcessedJobCount.Labels(job.Owner, jobRunner.Size).Inc();

        double secondsAlive = (DateTime.UtcNow - jobRunner.CreationQueuedTime).TotalSeconds;
        TotalMachineTime.Labels(job.Owner, jobRunner.Size).Inc(secondsAlive);
        
    }

    private static async Task JobInProgress(JsonElement workflowJson, ILogger<Program> logger, long jobId,
        string repoName, string orgName)
    {
        string runnerName = workflowJson.GetProperty("runner_name").GetString();
        logger.LogInformation($"Workflow Job {jobId} in repo {repoName} now in progress on {runnerName}");
        
        // Make the connection between the job and the runner in the DB
        var db = new ActionsRunnerContext();
        Runner runner = await db.LinkJobToRunner(jobId, runnerName);
       
        // Metrics
        PickedJobCount.Labels(orgName, runner.Size).Inc();

    }

    private static async Task JobQueued(ILogger<Program> logger, string repoName, List<string> labels, string orgName, RunnerQueue poolMgr, TargetType targetType, long jobId, string jobUrl)
    {
        logger.LogInformation($"New Workflow Job was queued for {repoName}. Queuing VM creation to replenish pool...");
        
        string size = string.Empty;
        string arch = string.Empty;
        string profileName = "default";
        bool isCustom = false;

        // Check if this is a custom run
        if (labels.Contains($"self-hosted-{Config.RunnerPrefix}-custom"))
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
            if (labels.Contains(csize.Name) || labels.Contains($"self-hosted-{Config.RunnerPrefix}-{csize.Name}"))
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

        string owner = targetType switch
        {
            TargetType.Organization => orgName,
            TargetType.Repository => repoName,
            _ => throw new ArgumentOutOfRangeException(nameof(targetType), targetType, null)
        };
        // Record runner to database
        await using var db = new ActionsRunnerContext();
        if (jobId > 0)
        {
            Job queuedJob = new()
            {
                GithubJobId = jobId,
                Repository = repoName,
                Owner = owner,
                State = JobState.Queued,
                QueueTime = DateTime.UtcNow,
                JobUrl = jobUrl,
                Orphan = false,
                RequestedProfile = profileName,
                RequestedSize = size
            };
            await db.Jobs.AddAsync(queuedJob);
        }

        Runner newRunner = new()
        {
            Size = size,
            Cloud = "htz",
            Hostname = "Unknown",
            Profile = profileName,
            Lifecycle =
            [
                new RunnerLifecycle
                {
                    EventTimeUtc = DateTime.UtcNow,
                    Status = RunnerStatus.CreationQueued,
                    Event = "Created as queued job runner"
                }
            ],
            IsOnline = false,
            Arch = arch,
            IPv4 = string.Empty,
            IsCustom = isCustom,
            Owner = owner
            
        };
        await db.Runners.AddAsync(newRunner);
        await db.SaveChangesAsync();
        
        poolMgr.CreateTasks.Enqueue(new CreateRunnerTask
        {
            RepoName = repoName,
            TargetType = targetType,
            RunnerDbId = newRunner.RunnerId
        });

        QueuedJobCount.Labels(orgName, size).Inc();
    }
}