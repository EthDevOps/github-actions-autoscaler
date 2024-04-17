namespace GithubActionsOrchestrator;

public class WebhookRedeliverService : IHostedService, IDisposable
{
    private int _executionCount = 0;
    private readonly ILogger<WebhookRedeliverService> _logger;
    private Timer? _timer = null;

    public WebhookRedeliverService(ILogger<WebhookRedeliverService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Webhook redeliver service running.");

        _timer = new Timer(DoWork, null, TimeSpan.Zero,
            TimeSpan.FromMinutes(5));

        return Task.CompletedTask;
    }

    private void DoWork(object? state)
    {
        var count = Interlocked.Increment(ref _executionCount);
        _logger.LogInformation(
            "Webhook redeliver Service is checking... Count: {Count}", count);
    }

    public Task StopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Webhook redeliver Service is stopping.");
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}