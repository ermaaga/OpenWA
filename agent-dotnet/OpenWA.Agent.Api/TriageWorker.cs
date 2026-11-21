using OpenWA.Agent.Infrastructure.Triage;

/// <summary>Every 15 seconds sends the owner the triage summaries that are due.</summary>
public sealed class TriageWorker : BackgroundService
{
    private readonly TriageService _triage;
    private readonly ILogger<TriageWorker> _logger;

    public TriageWorker(TriageService triage, ILogger<TriageWorker> logger)
    {
        _triage = triage;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await _triage.ProcessDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Triage worker iteration failed");
            }
        }
    }
}
