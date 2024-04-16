using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Prometheus;

namespace GithubActionsOrchestrator;

public class Program
{
    public static AutoScalerConfiguration Config = new();
    
    private static readonly Counter ProcessedJobCount = Metrics
        .CreateCounter("github_autoscaler_jobs_processed", "Number of processed jobs", labelNames: ["org","size"]);
    
    private static readonly Counter QueuedJobCount = Metrics
        .CreateCounter("github_autoscaler_jobs_queued", "Number of queued jobs", labelNames: ["org","size"]);
    
    private static readonly Counter PickedJobCount = Metrics
        .CreateCounter("github_autoscaler_jobs_picked", "Number of jobs picked up by a runner", labelNames: ["org","size"]);
    
    private static readonly Counter MachineCreatedCount = Metrics
        .CreateCounter("github_machines_created", "Number of created machines", labelNames: ["org","size"]);
    
    private static readonly Counter TotalMachineTime = Metrics
        .CreateCounter("github_total_machine_time", "Number of seconds machines were alive", labelNames: ["org","size"]);
    
    public static void Main(string[] args)
    {
        string persistPath = Environment.GetEnvironmentVariable("PERSIST_DIR") ?? Directory.CreateTempSubdirectory().FullName; 
        string configDir = Environment.GetEnvironmentVariable("CONFIG_DIR") ?? Directory.CreateTempSubdirectory().FullName; 

        // Setup pool config
        string configPath = Path.Combine(configDir, "config.json");
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"[ERR] Unable to read config file at {configPath}");
            return;
        }
        string configJson = File.ReadAllText(configPath);
        Config = JsonSerializer.Deserialize<AutoScalerConfiguration>(configJson) ?? throw new Exception("Unable to parse configuration");

        if (string.IsNullOrWhiteSpace(Config.HetznerToken))
        {
            Console.WriteLine($"[ERR] Hetzner cloud token not set in {configPath}");
            return;
        }
        
        Console.WriteLine($"[INIT] Loaded {Config.OrgConfigs.Count} orgs and {Config.Sizes.Count} sizes.");
        
        // Prepare metrics
        using var server = new KestrelMetricServer(port: 9000);
        server.Start();
        Console.WriteLine("[INIT] Metrics server listening on port 9000");
        
        // Init count metrics
        foreach (var org in Config.OrgConfigs)
        {
            foreach (var ms in Config.Sizes)
            {
               TotalMachineTime.Labels(org.OrgName, ms.Name).IncTo(0); 
               MachineCreatedCount.Labels(org.OrgName, ms.Name).IncTo(0); 
               PickedJobCount.Labels(org.OrgName, ms.Name).IncTo(0); 
               QueuedJobCount.Labels(org.OrgName, ms.Name).IncTo(0); 
               ProcessedJobCount.Labels(org.OrgName, ms.Name).IncTo(0); 
            }
        }
        
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddHostedService<PoolManager>();

        // Add services to the container.
        builder.Services.AddAuthorization();

        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        builder.Services.AddSingleton<CloudController>(svc =>
        {
            var logger = svc.GetRequiredService<ILogger<CloudController>>();
            return new CloudController(logger, Config.HetznerToken, persistPath,Config.Sizes, Config.ProvisionScriptBaseUrl, Config.MetricUser, Config.MetricPassword);
        });

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.UseAuthorization();

        // Prepare pools
        
        
        app.MapPost("/github-webhook", async (HttpRequest request, [FromServices] CloudController cloud, [FromServices] ILogger<Program> logger) =>
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
                    return;
                }

                if (!json.RootElement.TryGetProperty("workflow_job", out var workflowJson))
                {
                    logger.LogWarning("Received a non-workflowJob request. Ignoring.");
                    return;
                }
                
                List<string?> labels = workflowJson.GetProperty("labels").EnumerateArray()
                    .Select(x => x.GetString()).ToList();

                bool isSelfHosted = labels.Any(x => x.StartsWith("self-hosted"));
                
                if (!isSelfHosted)
                {
                    logger.LogInformation($"Received a non self-hosted request. Ignoring. Labels: {string.Join('|', labels)}");
                    return;
                }

                long jobId = workflowJson.GetProperty("id").GetInt64();
                string repoName = json.RootElement.GetProperty("repository").GetProperty("full_name").GetString();
                string orgNameReq = json.RootElement.GetProperty("organization").GetProperty("login").GetString();
              
                string orgName = Config.OrgConfigs.FirstOrDefault(x => x.OrgName.ToLower() == orgNameReq.ToLower())?.OrgName;
                if (String.IsNullOrEmpty(orgName))
                {
                    logger.LogError($"Unknown organization: {orgName} - check setup. aborting.");
                    return;
                }
                
                // Check if Org is configured
                if (Config.OrgConfigs.All(x => x.OrgName != orgName))
                {
                    logger.LogWarning($"GitHub Org {orgName} is not configured. Ignoring.");
                    return;
                }
                
                switch (action)
                {
                    case "queued":
                        logger.LogInformation($"New Workflow Job was queued for {repoName}. Creating VM to replenish pool...");

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
                            if (labels.Contains($"{csize.Name}-x64")|| labels.Contains($"self-hosted-{csize.Name}-x64"))
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
                        
                        
                        string runnerToken = await GetRunnerToken(githubToken, orgName);
                        if (String.IsNullOrEmpty(runnerToken))
                        {
                            logger.LogError("Unable to get new runner token. aborting.");
                            return;
                        }
                        
                        string newRunner = await cloud.CreateNewRunner(arch, size, runnerToken, orgName);
                        logger.LogInformation($"New Runner {newRunner} [{size} on {arch}] entering pool.");
                        MachineCreatedCount.Labels(orgName, size).Inc();
                        QueuedJobCount.Labels(orgName, size).Inc();

                        break;
                    case "in_progress":
                        string? runnerName = workflowJson.GetProperty("runner_name").GetString();
                        string? jobUrl = workflowJson.GetProperty("url").GetString();
                        logger.LogInformation($"Workflow Job {jobId} now in progress on {runnerName}");
                        cloud.AddJobClaimToRunner(runnerName, jobId, jobUrl, repoName);

                        string jobSize = cloud.GetInfoForJob(jobId)?.Size;
                        PickedJobCount.Labels(orgName, jobSize).Inc();
                        break;
                    case "completed":
                        logger.LogInformation($"Workflow Job {jobId} has completed. Deleting VM associated with Job...");
                        var vm = cloud.GetInfoForJob(jobId);
                        if (vm == null)
                        {
                            logger.LogError($"No VM on record for JobID: {jobId}");    
                        }
                        else
                        {
                            await cloud.DeleteRunner(vm.Id);
                            ProcessedJobCount.Labels(vm.OrgName, vm.Size).Inc();

                            double secondsAlive = (DateTime.UtcNow - vm.CreatedAt).TotalSeconds;
                            TotalMachineTime.Labels(vm.OrgName, vm.Size).Inc(secondsAlive);
                        }
                        break;
                    default:
                        logger.LogWarning("Unknown action. Ignoring");
                        break;
                }
            })
            .WithName("GithubWebhook")
            .WithOpenApi();

        app.Run();
    }

    public static async Task<string> GetRunnerToken(string githubToken, string orgName)
    {
        
        // Register a runner with github
        string runnerToken = string.Empty;
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"Bearer {githubToken}");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("hetzner-autoscale", "1")); 
            
        var response = await client.PostAsync(
            $"https://api.github.com/orgs/{orgName}/actions/runners/registration-token", null);

        if(response.IsSuccessStatusCode)
        {
            string content = await response.Content.ReadAsStringAsync();
            var responseObject = JsonSerializer.Deserialize<GitHubResponse>(content);
            if (responseObject == null)
            {
                return null;
            }
            return responseObject.token;
        }
        else
        {
            return null;
        }
    }
}
