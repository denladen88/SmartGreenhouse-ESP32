using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartGreenhouse.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddActuatorRuntimeTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AirHeaterRuntimeMs",
                table: "Telemetries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ExhaustFanRuntimeMs",
                table: "Telemetries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "FanRuntimeMs",
                table: "Telemetries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LightRuntimeMs",
                table: "Telemetries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "PumpRuntimeMs",
                table: "Telemetries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SoilHeaterRuntimeMs",
                table: "Telemetries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AirHeaterRuntimeMs",
                table: "Telemetries");

            migrationBuilder.DropColumn(
                name: "ExhaustFanRuntimeMs",
                table: "Telemetries");

            migrationBuilder.DropColumn(
                name: "FanRuntimeMs",
                table: "Telemetries");

            migrationBuilder.DropColumn(
                name: "LightRuntimeMs",
                table: "Telemetries");

            migrationBuilder.DropColumn(
                name: "PumpRuntimeMs",
                table: "Telemetries");

            migrationBuilder.DropColumn(
                name: "SoilHeaterRuntimeMs",
                table: "Telemetries");
        }
    }
}
