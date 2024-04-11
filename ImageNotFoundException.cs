namespace GithubActionsOrchestrator;

public class ImageNotFoundException : Exception
{
    public ImageNotFoundException()
    {
    }

    public ImageNotFoundException(string message) : base(message)
    {
    }

    public ImageNotFoundException(string message, Exception inner) : base(message, inner)
    {
    }
}