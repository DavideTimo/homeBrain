using CasaTimo.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CasaTimo.Infrastructure.Services;

public class ConnectorStatusReporter(IServiceScopeFactory scopeFactory)
{
    public async Task ReportAsync(string connectorName, bool healthy, string? error, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CasaTimoDbContext>();
        var config = await db.ConnectorConfigs
            .FirstOrDefaultAsync(c => c.ConnectorName == connectorName, ct);
        if (config == null) return;
        config.LastPollAt = DateTime.UtcNow;
        config.IsHealthy = healthy;
        config.LastError = healthy ? null : error;
        await db.SaveChangesAsync(ct);
    }
}
