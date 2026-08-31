using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartGreenhouse.Backend.Data;
using SmartGreenhouse.Backend.Models;

namespace SmartGreenhouse.Backend.Services;

// Таблиці Telemetries/AiDecisions ростуть безмежно (кількасот рядків/добу при
// 3-хв публікації). Раз на SweepIntervalHours видаляє все, старіше за
// RetentionDays. PlantProfiles/Plantings не чіпає — вони малі й значущі.
// Перший прохід — одразу при старті (прибирає накопичений хвіст).
public class DataRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DataRetentionService> _logger;
    private readonly DataRetentionOptions _options;

    public DataRetentionService(
        IServiceScopeFactory scopeFactory,
        ILogger<DataRetentionService> logger,
        IOptions<DataRetentionOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, _options.SweepIntervalHours)));
        do
        {
            await SweepAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(Math.Max(1, _options.RetentionDays));

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var telemetryRemoved = await db.Telemetries.Where(t => t.Timestamp < cutoff).ExecuteDeleteAsync(ct);
            var decisionsRemoved = await db.AiDecisions.Where(d => d.Timestamp < cutoff).ExecuteDeleteAsync(ct);

            if (telemetryRemoved > 0 || decisionsRemoved > 0)
            {
                _logger.LogInformation(
                    "Data retention sweep: removed {Telemetry} telemetry + {Decisions} decision rows older than {Days}d",
                    telemetryRemoved, decisionsRemoved, _options.RetentionDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Data retention sweep failed");
        }
    }
}
