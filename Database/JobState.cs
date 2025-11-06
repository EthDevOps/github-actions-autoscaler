namespace GithubActionsOrchestrator.Database;

public enum JobState
{
    Unknown = 0,
    Queued = 1,
    InProgress = 2,
    Completed = 3,
    Vanished = 4,
    Cancelled = 5,
    Throttled = 6  // Job is queued but waiting due to runner quota limit
}