namespace GithubActionsOrchestrator.Database;

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