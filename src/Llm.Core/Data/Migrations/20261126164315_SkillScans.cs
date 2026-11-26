using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SkillScans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ScanAllowedById",
                table: "skills",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScanHash",
                table: "skills",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScanRecommendation",
                table: "skills",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScanReport",
                table: "skills",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScanScore",
                table: "skills",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ScannedAt",
                table: "skills",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_skills_ScanAllowedById",
                table: "skills",
                column: "ScanAllowedById");

            migrationBuilder.AddForeignKey(
                name: "FK_skills_AspNetUsers_ScanAllowedById",
                table: "skills",
                column: "ScanAllowedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_skills_AspNetUsers_ScanAllowedById",
                table: "skills");

            migrationBuilder.DropIndex(
                name: "IX_skills_ScanAllowedById",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "ScanAllowedById",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "ScanHash",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "ScanRecommendation",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "ScanReport",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "ScanScore",
                table: "skills");

            migrationBuilder.DropColumn(
                name: "ScannedAt",
                table: "skills");
        }
    }
}
