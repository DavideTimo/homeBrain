using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CasaTimo.Infrastructure.Migrations;

public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ConnectorConfigs",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ConnectorName = table.Column<string>(type: "TEXT", nullable: false),
                SettingsJson = table.Column<string>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ConnectorConfigs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Devices",
            columns: table => new
            {
                Id = table.Column<string>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Type = table.Column<string>(type: "TEXT", nullable: true),
                Location = table.Column<string>(type: "TEXT", nullable: true),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Devices", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Bills",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Type = table.Column<int>(type: "INTEGER", nullable: false),
                Issuer = table.Column<string>(type: "TEXT", nullable: false),
                Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                DueDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                PeriodFrom = table.Column<DateTime>(type: "TEXT", nullable: true),
                PeriodTo = table.Column<DateTime>(type: "TEXT", nullable: true),
                PdfPath = table.Column<string>(type: "TEXT", nullable: true),
                EmailId = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                IsPaid = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Bills", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SensorReadings",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                Metric = table.Column<string>(type: "TEXT", nullable: false),
                Value = table.Column<double>(type: "REAL", nullable: false),
                Unit = table.Column<string>(type: "TEXT", nullable: true),
                Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SensorReadings", x => x.Id);
                table.ForeignKey(
                    name: "FK_SensorReadings_Devices_DeviceId",
                    column: x => x.DeviceId,
                    principalTable: "Devices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MaintenanceRecords",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                Cost = table.Column<decimal>(type: "TEXT", nullable: true),
                NextDueDate = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MaintenanceRecords", x => x.Id);
                table.ForeignKey(
                    name: "FK_MaintenanceRecords_Devices_DeviceId",
                    column: x => x.DeviceId,
                    principalTable: "Devices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Reminders",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                BillId = table.Column<long>(type: "INTEGER", nullable: false),
                DueDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                DaysBefore = table.Column<int>(type: "INTEGER", nullable: false),
                IsSent = table.Column<bool>(type: "INTEGER", nullable: false),
                Message = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Reminders", x => x.Id);
                table.ForeignKey(
                    name: "FK_Reminders_Bills_BillId",
                    column: x => x.BillId,
                    principalTable: "Bills",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SensorReadings_DeviceId",
            table: "SensorReadings",
            column: "DeviceId");

        migrationBuilder.CreateIndex(
            name: "IX_SensorReadings_Timestamp",
            table: "SensorReadings",
            column: "Timestamp");

        migrationBuilder.CreateIndex(
            name: "IX_MaintenanceRecords_DeviceId",
            table: "MaintenanceRecords",
            column: "DeviceId");

        migrationBuilder.CreateIndex(
            name: "IX_Reminders_BillId",
            table: "Reminders",
            column: "BillId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SensorReadings");
        migrationBuilder.DropTable(name: "MaintenanceRecords");
        migrationBuilder.DropTable(name: "Reminders");
        migrationBuilder.DropTable(name: "Bills");
        migrationBuilder.DropTable(name: "Devices");
        migrationBuilder.DropTable(name: "ConnectorConfigs");
    }
}
