namespace GithubActionsOrchestrator.CloudControllers;

public class UnsupportedMachineTypeException : Exception
{
    public UnsupportedMachineTypeException()
    {
    }

    public UnsupportedMachineTypeException(string message) : base(message)
    {
    }

    public UnsupportedMachineTypeException(string message, Exception inner) : base(message, inner)
    {
    }
}