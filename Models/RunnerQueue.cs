using System.Collections.Concurrent;

namespace GithubActionsOrchestrator.Models;

public class RunnerQueue
{
    public ConcurrentQueue<CreateRunnerTask> CreateTasks { get; } = new();
    public ConcurrentQueue<DeleteRunnerTask> DeleteTasks { get; } = new();

    public ConcurrentDictionary<string, CreateRunnerTask> CreatedRunners { get; } = new();

    public ConcurrentDictionary<(string Owner, string Repository, string Size, string Profile, string Arch), int> CancelledRunners { get; } = new();
}