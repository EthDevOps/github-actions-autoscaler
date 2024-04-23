using System.Collections.Concurrent;

namespace GithubActionsOrchestrator;

public class RunnerQueue
{
    
    public ConcurrentQueue<CreateRunnerTask?> CreateTasks { get; }
    public ConcurrentQueue<DeleteRunnerTask?> DeleteTasks { get; }

    public RunnerQueue()
    {
        CreateTasks = new ConcurrentQueue<CreateRunnerTask>();
        DeleteTasks = new ConcurrentQueue<DeleteRunnerTask>();
    }
}