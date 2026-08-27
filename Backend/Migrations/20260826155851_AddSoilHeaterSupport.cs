using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartGreenhouse.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddSoilHeaterSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "SoilTempC",
                table: "Telemetries",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SoilTempMinC",
                table: "PlantProfiles",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "SoilHeaterPower",
                table: "AiDecisions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SoilTempC",
                table: "Telemetries");

            migrationBuilder.DropColumn(
                name: "SoilTempMinC",
                table: "PlantProfiles");

            migrationBuilder.DropColumn(
                name: "SoilHeaterPower",
                table: "AiDecisions");
        }
    }
}
