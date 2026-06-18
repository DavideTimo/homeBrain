using CasaTimo.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CasaTimo.Api.Tests;

public class TestApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Inject test-specific config values before the host builds
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"]        = "test-secret-key-for-testing-min-32-chars-long!",
                ["Jwt:Issuer"]     = "casatimo-api",
                ["Jwt:Audience"]   = "casatimo-clients",
                ["AdminPassword"]  = "test_admin"
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace SQLite with InMemory so tests don't touch the filesystem
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<CasaTimoDbContext>));
            if (descriptor != null) services.Remove(descriptor);

            services.AddDbContext<CasaTimoDbContext>(opts =>
                opts.UseInMemoryDatabase("TestDb_" + Guid.NewGuid()));
        });
    }
}
