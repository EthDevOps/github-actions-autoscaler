using System.Collections.Concurrent;

namespace GithubActionsOrchestrator.Models;

public class RunnerQueue
{
    public ConcurrentQueue<CreateRunnerTask> CreateTasks { get; } = new();
    public ConcurrentQueue<DeleteRunnerTask> DeleteTasks { get; } = new();

    public ConcurrentDictionary<string, CreateRunnerTask> CreatedRunners { get; } = new();
}