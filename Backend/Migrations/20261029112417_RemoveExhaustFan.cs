using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartGreenhouse.Backend.Migrations
{
    /// <inheritdoc />
    public partial class RemoveExhaustFan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExhaustFanRuntimeMs",
                table: "Telemetries");

            migrationBuilder.DropColumn(
                name: "AirCoolingActive",
                table: "ControlRecoveryStates");

            migrationBuilder.DropColumn(
                name: "ExhaustFanOn",
                table: "AiDecisions");

            migrationBuilder.DropColumn(
                name: "ExhaustFanReason",
                table: "AiDecisions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ExhaustFanRuntimeMs",
                table: "Telemetries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "AirCoolingActive",
                table: "ControlRecoveryStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ExhaustFanOn",
                table: "AiDecisions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ExhaustFanReason",
                table: "AiDecisions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}
