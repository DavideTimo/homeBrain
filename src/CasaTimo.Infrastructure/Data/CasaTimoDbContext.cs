using CasaTimo.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace CasaTimo.Infrastructure.Data;

public class CasaTimoDbContext : DbContext
{
    public CasaTimoDbContext(DbContextOptions<CasaTimoDbContext> options) : base(options) { }

    public DbSet<SensorReading> SensorReadings => Set<SensorReading>();
    public DbSet<Bill> Bills => Set<Bill>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<MaintenanceRecord> MaintenanceRecords => Set<MaintenanceRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SensorReading>(e =>
        {
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => new { x.DeviceId, x.Metric });
        });

        modelBuilder.Entity<Bill>(e =>
        {
            e.HasMany(x => x.Reminders).WithOne(x => x.Bill).HasForeignKey(x => x.BillId);
            e.HasIndex(x => x.DueDate);
            e.HasIndex(x => x.Type);
        });

        modelBuilder.Entity<Reminder>(e =>
        {
            e.HasIndex(x => new { x.IsSent, x.DueDate });
        });
    }
}
