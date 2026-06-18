using CasaTimo.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CasaTimo.Infrastructure.Data;

public class CasaTimoDbContext : DbContext
{
    public CasaTimoDbContext(DbContextOptions<CasaTimoDbContext> options) : base(options)
    {
    }

    public DbSet<SensorReading> SensorReadings { get; set; }
    public DbSet<Device> Devices { get; set; }
    public DbSet<Bill> Bills { get; set; }
    public DbSet<Reminder> Reminders { get; set; }
    public DbSet<MaintenanceRecord> MaintenanceRecords { get; set; }
    public DbSet<CasaTimo.Core.Models.ConnectorConfig> ConnectorConfigs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Device>().HasKey(d => d.Id);

        modelBuilder.Entity<SensorReading>().HasKey(r => r.Id);
        modelBuilder.Entity<SensorReading>()
            .HasIndex(r => new { r.DeviceId, r.Timestamp });

        modelBuilder.Entity<Bill>()
            .HasIndex(b => b.EmailId)
            .IsUnique()
            .HasFilter("[EmailId] IS NOT NULL");
    }
}
