using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartGreenhouse.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddMidpointRecoveryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ControlRecoveryStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlantName = table.Column<string>(type: "TEXT", nullable: false),
                    AirHeatingActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    AirCoolingActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    AirDryingActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SoilHeatingActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SoilWateringActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SoilDryingActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ControlRecoveryStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ControlRecoveryStates_PlantName",
                table: "ControlRecoveryStates",
                column: "PlantName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ControlRecoveryStates");
        }
    }
}
