// Create an extension method for formatting exception details

using System.Text;

public static class ExceptionExtensions
{
    public static string GetFullExceptionDetails(this Exception ex)
    {
        var sb = new StringBuilder();
        var currentEx = ex;
        var level = 0;
        
        while (currentEx != null)
        {
            if (level > 0)
            {
                sb.AppendLine($"Inner Exception Level {level}:");
            }
            sb.AppendLine($"Message: {currentEx.Message}");
            sb.AppendLine($"Stack Trace: {currentEx.StackTrace}");
            
            currentEx = currentEx.InnerException;
            level++;
            
            if (currentEx != null)
            {
                sb.AppendLine();
            }
        }
        
        return sb.ToString();
    }
}
