using FlyScreen.Relay.Options;
using Microsoft.Extensions.Options;

namespace FlyScreen.Relay.Services;

public sealed class MaintenanceService : BackgroundService
{
    private readonly RelaySessionManager _sessions;
    private readonly IOptionsMonitor<RelayOptions> _options;
    private readonly ILogger<MaintenanceService> _log;

    public MaintenanceService(
        RelaySessionManager sessions,
        IOptionsMonitor<RelayOptions> options,
        ILogger<MaintenanceService> log)
    {
        _sessions = sessions;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
                var idle = TimeSpan.FromSeconds(Math.Max(60, _options.CurrentValue.RelayIdleTimeoutSeconds));
                var n = _sessions.SweepIdle(idle);
                if (n > 0)
                    _log.LogInformation("清理空闲中继会话 {Count}", n);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "维护任务异常");
            }
        }
    }
}
