using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CasaTimo.Infrastructure.Migrations;

public partial class AddPushSubscriptions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PushSubscriptions",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Endpoint = table.Column<string>(type: "TEXT", nullable: false),
                P256dh = table.Column<string>(type: "TEXT", nullable: true),
                Auth = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
            },
            constraints: table => table.PrimaryKey("PK_PushSubscriptions", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_PushSubscriptions_Endpoint",
            table: "PushSubscriptions",
            column: "Endpoint",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "PushSubscriptions");
}
