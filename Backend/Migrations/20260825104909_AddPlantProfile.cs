using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartGreenhouse.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddPlantProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlantProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlantName = table.Column<string>(type: "TEXT", nullable: false),
                    TempMinC = table.Column<double>(type: "REAL", nullable: false),
                    TempMaxC = table.Column<double>(type: "REAL", nullable: false),
                    HumidityMinPct = table.Column<double>(type: "REAL", nullable: false),
                    HumidityMaxPct = table.Column<double>(type: "REAL", nullable: false),
                    SoilMoistureMinPct = table.Column<double>(type: "REAL", nullable: false),
                    SoilMoistureMaxPct = table.Column<double>(type: "REAL", nullable: false),
                    DailyLightHoursTarget = table.Column<double>(type: "REAL", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: false),
                    LastUpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUpdateReason = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlantProfiles", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlantProfiles");
        }
    }
}
