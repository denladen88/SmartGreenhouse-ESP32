using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartGreenhouse.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddSoilValidToTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue: true (не false) — старі рядки передують цьому полю і не
            // повинні раптом зникнути з тренду вологості ґрунту, яким керується
            // помпа/просушка в RunLocalControlAsync (та сама пастка, що й у
            // AddExhaustFanSupport: backfill у false там на один тік зіпсував
            // гістерезис витяжки одразу після деплою).
            migrationBuilder.AddColumn<bool>(
                name: "SoilValid",
                table: "Telemetries",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SoilValid",
                table: "Telemetries");
        }
    }
}
