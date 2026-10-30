using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartGreenhouse.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddFanCooling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AirCoolingActive",
                table: "ControlRecoveryStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "LedFanOn",
                table: "AiDecisions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LedFanReason",
                table: "AiDecisions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AirCoolingActive",
                table: "ControlRecoveryStates");

            migrationBuilder.DropColumn(
                name: "LedFanOn",
                table: "AiDecisions");

            migrationBuilder.DropColumn(
                name: "LedFanReason",
                table: "AiDecisions");
        }
    }
}
