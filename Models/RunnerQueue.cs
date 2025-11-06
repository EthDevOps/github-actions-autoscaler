using System.Collections.Concurrent;
using System.Linq.Expressions;
using GithubActionsOrchestrator.Database;
using Microsoft.EntityFrameworkCore;

namespace GithubActionsOrchestrator.Models;

public class RunnerQueue
{
    public RunnerQueue()
    {
        CreateTasks = new DatabaseCreateTaskQueue();
        DeleteTasks = new DatabaseDeleteTaskQueue();
        CreatedRunners = new DatabaseCreatedRunnersDictionary();
        CancelledRunners = new DatabaseCancelledRunnersDictionary();
    }

    public DatabaseCreateTaskQueue CreateTasks { get; }
    public DatabaseDeleteTaskQueue DeleteTasks { get; }
    public DatabaseCreatedRunnersDictionary CreatedRunners { get; }
    public DatabaseCancelledRunnersDictionary CancelledRunners { get; }
}

// Wrapper for CreateTaskQueue to mimic ConcurrentQueue<CreateRunnerTask>
public class DatabaseCreateTaskQueue
{
    public int Count
    {
        get
        {
            using var context = new ActionsRunnerContext();
            return context.CreateTaskQueues.Count();
        }
    }

    public bool Any()
    {
        using var context = new ActionsRunnerContext();
        return context.CreateTaskQueues.Any();
    }

    public bool Any(Expression<Func<CreateTaskQueue, bool>> predicate)
    {
        using var context = new ActionsRunnerContext();
        return context.CreateTaskQueues.Any(predicate);
    }

    public int CountWhere(Expression<Func<CreateTaskQueue, bool>> predicate)
    {
        using var context = new ActionsRunnerContext();
        return context.CreateTaskQueues.Count(predicate);
    }

    public void Enqueue(CreateRunnerTask task)
    {
        using var context = new ActionsRunnerContext();
        var queueItem = new CreateTaskQueue
        {
            RetryCount = task.RetryCount,
            TargetType = task.TargetType,
            RepoName = task.RepoName,
            RunnerDbId = task.RunnerDbId,
            IsStuckReplacement = task.IsStuckReplacement,
            StuckJobId = task.StuckJobId,
            QueuedAt = DateTime.UtcNow
        };
        context.CreateTaskQueues.Add(queueItem);
        context.SaveChanges();
    }

    public bool TryDequeue(out CreateRunnerTask? task)
    {
        using var context = new ActionsRunnerContext();
        var queueItem = context.CreateTaskQueues
            .OrderBy(x => x.CreateTaskQueueId)
            .FirstOrDefault();

        if (queueItem == null)
        {
            task = null;
            return false;
        }

        task = new CreateRunnerTask
        {
            RetryCount = queueItem.RetryCount,
            TargetType = queueItem.TargetType,
            RepoName = queueItem.RepoName,
            RunnerDbId = queueItem.RunnerDbId,
            IsStuckReplacement = queueItem.IsStuckReplacement,
            StuckJobId = queueItem.StuckJobId
        };

        context.CreateTaskQueues.Remove(queueItem);
        context.SaveChanges();
        return true;
    }
}

// Wrapper for DeleteTaskQueue to mimic ConcurrentQueue<DeleteRunnerTask>
public class DatabaseDeleteTaskQueue
{
    public int Count
    {
        get
        {
            using var context = new ActionsRunnerContext();
            return context.DeleteTaskQueues.Count();
        }
    }

    public void Enqueue(DeleteRunnerTask task)
    {
        using var context = new ActionsRunnerContext();
        var queueItem = new DeleteTaskQueue
        {
            RetryCount = task.RetryCount,
            ServerId = task.ServerId,
            RunnerDbId = task.RunnerDbId,
            QueuedAt = DateTime.UtcNow
        };
        context.DeleteTaskQueues.Add(queueItem);
        context.SaveChanges();
    }

    public bool TryDequeue(out DeleteRunnerTask? task)
    {
        using var context = new ActionsRunnerContext();
        var queueItem = context.DeleteTaskQueues
            .OrderBy(x => x.DeleteTaskQueueId)
            .FirstOrDefault();

        if (queueItem == null)
        {
            task = null;
            return false;
        }

        task = new DeleteRunnerTask
        {
            RetryCount = queueItem.RetryCount,
            ServerId = queueItem.ServerId,
            RunnerDbId = queueItem.RunnerDbId
        };

        context.DeleteTaskQueues.Remove(queueItem);
        context.SaveChanges();
        return true;
    }
}

// Wrapper for CreatedRunnersTracking to mimic ConcurrentDictionary<string, CreateRunnerTask>
public class DatabaseCreatedRunnersDictionary
{
    public int Count
    {
        get
        {
            using var context = new ActionsRunnerContext();
            return context.CreatedRunnersTrackings.Count();
        }
    }

    public bool TryAdd(string hostname, CreateRunnerTask task)
    {
        using var context = new ActionsRunnerContext();
        var existing = context.CreatedRunnersTrackings.Find(hostname);
        if (existing != null)
        {
            return false;
        }

        var tracking = new CreatedRunnersTracking
        {
            Hostname = hostname,
            RetryCount = task.RetryCount,
            TargetType = task.TargetType,
            RepoName = task.RepoName,
            RunnerDbId = task.RunnerDbId,
            IsStuckReplacement = task.IsStuckReplacement,
            StuckJobId = task.StuckJobId,
            CreatedAt = DateTime.UtcNow
        };
        context.CreatedRunnersTrackings.Add(tracking);
        context.SaveChanges();
        return true;
    }

    public bool TryRemove(string hostname, out CreateRunnerTask? task)
    {
        using var context = new ActionsRunnerContext();
        var tracking = context.CreatedRunnersTrackings.Find(hostname);
        if (tracking == null)
        {
            task = null;
            return false;
        }

        task = new CreateRunnerTask
        {
            RetryCount = tracking.RetryCount,
            TargetType = tracking.TargetType,
            RepoName = tracking.RepoName,
            RunnerDbId = tracking.RunnerDbId,
            IsStuckReplacement = tracking.IsStuckReplacement,
            StuckJobId = tracking.StuckJobId
        };

        context.CreatedRunnersTrackings.Remove(tracking);
        context.SaveChanges();
        return true;
    }

    public bool Remove(string hostname)
    {
        using var context = new ActionsRunnerContext();
        var tracking = context.CreatedRunnersTrackings.Find(hostname);
        if (tracking == null)
        {
            return false;
        }

        context.CreatedRunnersTrackings.Remove(tracking);
        context.SaveChanges();
        return true;
    }

    public bool Remove(string hostname, out CreateRunnerTask? task)
    {
        return TryRemove(hostname, out task);
    }
}

// Wrapper for CancelledRunnersCounter to mimic ConcurrentDictionary<(string, string, string, string, string), int>
public class DatabaseCancelledRunnersDictionary
{
    public int AddOrUpdate(
        (string Owner, string Repository, string Size, string Profile, string Arch) key,
        int addValue,
        Func<(string Owner, string Repository, string Size, string Profile, string Arch), int, int> updateValueFactory)
    {
        using var context = new ActionsRunnerContext();
        var (owner, repository, size, profile, arch) = key;

        var counter = context.CancelledRunnersCounters
            .FirstOrDefault(c => c.Owner == owner && c.Repository == repository &&
                                 c.Size == size && c.Profile == profile && c.Arch == arch);

        if (counter == null)
        {
            counter = new CancelledRunnersCounter
            {
                Owner = owner,
                Repository = repository,
                Size = size,
                Profile = profile,
                Arch = arch,
                Count = addValue
            };
            context.CancelledRunnersCounters.Add(counter);
        }
        else
        {
            counter.Count = updateValueFactory(key, counter.Count);
        }

        context.SaveChanges();
        return counter.Count;
    }
}