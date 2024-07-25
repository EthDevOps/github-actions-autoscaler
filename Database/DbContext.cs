namespace GithubActionsOrchestrator.Database;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;

public class ActionsRunnerContext()
    : DbContext
{
    public DbSet<Runner> Runners { get; set; }
    public DbSet<Workflow> Workflows { get; set; }
    public DbSet<RunnerLifecycle> RunnerLifecycles { get; set; }
    

    private readonly string _connectionString = $"Host=localhost;Username=postgres;Password=secret;Database=postgres";

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseNpgsql(_connectionString);
}

// Runners provisioned over time
public class Runner
{
    public int RunnerId { get; set; }
    public string Cloud { get; set; }
    public string Hostname { get; set; }
    public string Size { get; set; }
    public string Profile { get; set; }
    
    // Relations
    public Workflow Workflow { get; set; }
    public List<RunnerLifecycle> Lifecycle { get; set; }
    
}

public enum RunnerStatus
{
    Unknown = 0,
    Created = 1,
    Provisioned = 2,
    Processing = 3,
    Deleted = 4
}

public class RunnerLifecycle
{
    public int RunnerLifecycleId { get; set; }
    public DateTime EventTimeUtc { get; set; }
    public string Event { get; set; }
    public RunnerStatus Status { get; set; }
}

public class Workflow
{
    public int WorkflowId { get; set; }
    public long GithubWorkflowId { get; set; }
    public string Repository { get; set; }
    public string Owner { get; set; }
}
