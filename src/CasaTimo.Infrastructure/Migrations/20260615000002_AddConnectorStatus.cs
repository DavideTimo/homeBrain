using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CasaTimo.Infrastructure.Migrations;

public partial class AddConnectorStatus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "LastPollAt",
            table: "ConnectorConfigs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsHealthy",
            table: "ConnectorConfigs",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LastError",
            table: "ConnectorConfigs",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "LastPollAt", table: "ConnectorConfigs");
        migrationBuilder.DropColumn(name: "IsHealthy", table: "ConnectorConfigs");
        migrationBuilder.DropColumn(name: "LastError", table: "ConnectorConfigs");
    }
}
