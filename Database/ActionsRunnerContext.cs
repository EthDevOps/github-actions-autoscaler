using System.ComponentModel.DataAnnotations.Schema;
using GithubActionsOrchestrator.Models;

namespace GithubActionsOrchestrator.Database;
using Microsoft.EntityFrameworkCore;
using System;

public class ActionsRunnerContext()
    : DbContext
{
    public DbSet<Runner> Runners { get; set; }
    public DbSet<Job> Jobs { get; set; }
    public DbSet<RunnerLifecycle> RunnerLifecycles { get; set; }
    public DbSet<CreateTaskQueue> CreateTaskQueues { get; set; }
    public DbSet<DeleteTaskQueue> DeleteTaskQueues { get; set; }
    public DbSet<CreatedRunnersTracking> CreatedRunnersTrackings { get; set; }
    public DbSet<CancelledRunnersCounter> CancelledRunnersCounters { get; set; }

    
    
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

        // Configure CreatedRunnersTracking with Hostname as primary key
        modelBuilder.Entity<CreatedRunnersTracking>()
            .HasKey(c => c.Hostname);

        // Configure CancelledRunnersCounter with unique constraint on job specifications
        modelBuilder.Entity<CancelledRunnersCounter>()
            .HasIndex(c => new { c.Owner, c.Repository, c.Size, c.Profile, c.Arch })
            .IsUnique();
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