namespace GithubActionsOrchestrator.Database;

public enum JobState
{
    Unknown = 0,
    Queued = 1,
    InProgress = 2,
    Completed = 3,
    Vanished = 4
}