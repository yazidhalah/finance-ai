using FinanceAi.Infrastructure.Cases;

namespace FinanceAi.Api.Jobs;

/// <summary>The host for <see cref="SweepRunner"/> (slice 32): a short settle delay, then one pass per interval.</summary>
public sealed class SweepScheduler(SweepRunner runner, TimeProvider time, ILogger<SweepScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = SweepRunner.IntervalMinutes;
        if (interval == 0)
        {
            logger.LogInformation("Sweep scheduler disabled ({Variable}=0).", SweepRunner.IntervalVariable);
            return;
        }

        try { await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(interval), time);
        do
        {
            try
            {
                var outcomes = await runner.RunOnceAsync(stoppingToken);
                logger.LogInformation("Sweep pass: {Succeeded} tenant(s) swept, {Failed} failed.", outcomes.Count(o => o.Succeeded), outcomes.Count(o => !o.Succeeded));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sweep pass failed before any tenant ran.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
