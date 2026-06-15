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
    public DbSet<ConnectorConfig> ConnectorConfigs { get; set; }
    public DbSet<PushSubscription> PushSubscriptions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PushSubscription>().HasKey(p => p.Id);

        modelBuilder.Entity<Device>().HasKey(d => d.Id);

        modelBuilder.Entity<SensorReading>().HasKey(r => r.Id);
        modelBuilder.Entity<SensorReading>()
            .HasOne(r => r.Device)
            .WithMany()
            .HasForeignKey(r => r.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Reminder>()
            .HasOne(r => r.Bill)
            .WithMany()
            .HasForeignKey(r => r.BillId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MaintenanceRecord>()
            .HasOne(m => m.Device)
            .WithMany()
            .HasForeignKey(m => m.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Bill>()
            .Property(b => b.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder.Entity<ConnectorConfig>()
            .Property(c => c.UpdatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
    }
}
