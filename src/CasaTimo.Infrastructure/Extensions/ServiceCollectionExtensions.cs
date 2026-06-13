using CasaTimo.Core.Interfaces;
using CasaTimo.Infrastructure.Data;
using CasaTimo.Infrastructure.Mqtt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CasaTimo.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var dbPath = config["NAS_DB_PATH"] ?? "data";
        Directory.CreateDirectory(dbPath);

        services.AddDbContext<CasaTimoDbContext>(options =>
            options.UseSqlite($"Data Source={Path.Combine(dbPath, "casatimo.db")}"));

        services.AddSingleton<MqttClientService>();
        services.AddSingleton<IMqttService>(sp => sp.GetRequiredService<MqttClientService>());

        return services;
    }
}
