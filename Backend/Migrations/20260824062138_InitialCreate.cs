using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartGreenhouse.Backend.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PumpOn = table.Column<bool>(type: "INTEGER", nullable: false),
                    FanOn = table.Column<bool>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    PhotoDescription = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiDecisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Telemetries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    UptimeMs = table.Column<long>(type: "INTEGER", nullable: false),
                    TemperatureC = table.Column<double>(type: "REAL", nullable: true),
                    HumidityPct = table.Column<double>(type: "REAL", nullable: true),
                    PressureHpa = table.Column<double>(type: "REAL", nullable: true),
                    Lux = table.Column<double>(type: "REAL", nullable: true),
                    SoilRaw = table.Column<int>(type: "INTEGER", nullable: false),
                    SoilMoisturePct = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Telemetries", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiDecisions");

            migrationBuilder.DropTable(
                name: "Telemetries");
        }
    }
}
