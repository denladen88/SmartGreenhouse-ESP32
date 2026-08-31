using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartGreenhouse.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddTimestampIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Telemetries_Timestamp",
                table: "Telemetries",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AiDecisions_Timestamp",
                table: "AiDecisions",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Telemetries_Timestamp",
                table: "Telemetries");

            migrationBuilder.DropIndex(
                name: "IX_AiDecisions_Timestamp",
                table: "AiDecisions");
        }
    }
}
