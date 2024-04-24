using System.Collections.Concurrent;

namespace GithubActionsOrchestrator;

public class RunnerQueue
{
    public ConcurrentQueue<CreateRunnerTask> CreateTasks { get; } = new();
    public ConcurrentQueue<DeleteRunnerTask> DeleteTasks { get; } = new();
}