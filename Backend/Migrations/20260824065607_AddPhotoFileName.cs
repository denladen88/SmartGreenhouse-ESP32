using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartGreenhouse.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoFileName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PhotoFileName",
                table: "AiDecisions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PhotoFileName",
                table: "AiDecisions");
        }
    }
}
