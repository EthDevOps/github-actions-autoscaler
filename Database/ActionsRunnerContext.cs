using System.ComponentModel.DataAnnotations.Schema;

namespace GithubActionsOrchestrator.Database;
using Microsoft.EntityFrameworkCore;
using System;

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