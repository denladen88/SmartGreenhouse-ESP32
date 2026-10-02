using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartGreenhouse.Backend.Migrations
{
    /// <inheritdoc />
    public partial class AddAiExplainability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LastAiHadPhoto",
                table: "PlantProfiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LastAiPrompt",
                table: "PlantProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LastAiResponse",
                table: "PlantProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAiReviewedUtc",
                table: "PlantProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AirHeaterReason",
                table: "AiDecisions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ExhaustFanReason",
                table: "AiDecisions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FanReason",
                table: "AiDecisions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LightReason",
                table: "AiDecisions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PumpReason",
                table: "AiDecisions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SoilHeaterReason",
                table: "AiDecisions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "AiDecisions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastAiHadPhoto",
                table: "PlantProfiles");

            migrationBuilder.DropColumn(
                name: "LastAiPrompt",
                table: "PlantProfiles");

            migrationBuilder.DropColumn(
                name: "LastAiResponse",
                table: "PlantProfiles");

            migrationBuilder.DropColumn(
                name: "LastAiReviewedUtc",
                table: "PlantProfiles");

            migrationBuilder.DropColumn(
                name: "AirHeaterReason",
                table: "AiDecisions");

            migrationBuilder.DropColumn(
                name: "ExhaustFanReason",
                table: "AiDecisions");

            migrationBuilder.DropColumn(
                name: "FanReason",
                table: "AiDecisions");

            migrationBuilder.DropColumn(
                name: "LightReason",
                table: "AiDecisions");

            migrationBuilder.DropColumn(
                name: "PumpReason",
                table: "AiDecisions");

            migrationBuilder.DropColumn(
                name: "SoilHeaterReason",
                table: "AiDecisions");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "AiDecisions");
        }
    }
}
