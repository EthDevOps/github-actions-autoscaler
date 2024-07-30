using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace GithubActionsOrchestrator.Database;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;

public class ActionsRunnerContext()
    : DbContext
{
    public DbSet<Runner> Runners { get; set; }
    public DbSet<Job> Jobs { get; set; }
    public DbSet<RunnerLifecycle> RunnerLifecycles { get; set; }

    
    
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseNpgsql(Program.Config.DbConnectionString);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Runner>()
            .HasOne(r => r.Job)
            .WithOne()
            .HasForeignKey<Runner>(r => r.JobId)
            .IsRequired(false);

        modelBuilder.Entity<Job>()
            .HasOne(j => j.Runner)
            .WithOne()
            .HasForeignKey<Job>(j => j.RunnerId)
            .IsRequired(false);
    }

    public async Task<Runner> LinkJobToRunner(long jobId, string runnerName)
    {
        try
        {
            var job = await Jobs.Include(x => x.Runner).FirstOrDefaultAsync(x => x.GithubJobId == jobId);
            var runner = await Runners.Include(x => x.Job).Include(x => x.Lifecycle).FirstOrDefaultAsync(x => x.Hostname == runnerName);
            runner.Job = job;
            job.Runner = runner;
            job.InProgressTime = DateTime.UtcNow;
            runner.Lifecycle.Add(new()
            {
                Event = $"Runner got picked by job {jobId}",
                Status = RunnerStatus.Processing,
                EventTimeUtc = DateTime.UtcNow
            });
            await SaveChangesAsync();
            return runner;
        }
        catch
        {
            // unable to link
            return null;
        }

    }
    
}

// Runners provisioned over time
public class Runner
{
    public string IPv4 { get; set; }
    public int RunnerId { get; set; }
    public string Cloud { get; set; }
    public string Hostname { get; set; }
    public string Size { get; set; }
    public string Profile { get; set; }
    
    // Relations

    public int? JobId { get; set; }
    public Job Job { get; set; }
    public ICollection<RunnerLifecycle> Lifecycle { get; set; }
    public long CloudServerId { get; set; }

    public DateTime CreateTime
    {
        get
        {
            return Lifecycle.FirstOrDefault(x => x.Status == RunnerStatus.CreationQueued)!.EventTimeUtc;
        }
    }

    public RunnerStatus LastState
    {
        get
        {
            return Lifecycle.MaxBy(x => x.EventTimeUtc).Status;
        }
    }

    public bool IsOnline { get; set; }
    public string Arch { get; set; }
    public string Owner { get; set; }
    public bool IsCustom { get; set; }

    public DateTime LastStateTime
    {
        get
        {
           return Lifecycle.MaxBy(x => x.EventTimeUtc).EventTimeUtc;
        }
    }
}

public enum RunnerStatus
{
    Unknown = 0,
    CreationQueued = 1,
    Created = 2,
    Provisioned = 3,
    Processing = 4,
    DeletionQueued = 5,
    Deleted = 6,
    Failure = 7,
    VanishedOnCloud = 8,
    Cleanup = 9
}

public enum JobState
{
    Unknown = 0,
    Queued = 1,
    InProgress = 2,
    Completed = 3
}
public class RunnerLifecycle
{
    public int RunnerLifecycleId { get; set; }
    public DateTime EventTimeUtc { get; set; }
    public string Event { get; set; }
    public RunnerStatus Status { get; set; }
}

public class Job
{
    public int JobId { get; set; }
    public long GithubJobId { get; set; }
    public string Repository { get; set; }
    public string Owner { get; set; }
    public JobState State { get; set; }
    public DateTime InProgressTime { get; set; }
    public DateTime QueueTime { get; set; }
    public DateTime CompleteTime { get; set; }
    public string JobUrl { get; set; }
    
    //Relations
    public int? RunnerId { get; set; }

    [JsonIgnore]
    public Runner Runner { get; set; }
    public bool Orphan { get; set; }
    public string RequestedProfile { get; set; }
    public string RequestedSize { get; set; }
}
