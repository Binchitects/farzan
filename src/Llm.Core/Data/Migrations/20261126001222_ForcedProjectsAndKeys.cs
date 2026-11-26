using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ForcedProjectsAndKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Forced",
                table: "work_project_members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                schema: "gateway",
                table: "api_keys",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_project_id",
                schema: "gateway",
                table: "api_keys",
                column: "project_id");

            migrationBuilder.AddForeignKey(
                name: "FK_api_keys_work_projects_project_id",
                schema: "gateway",
                table: "api_keys",
                column: "project_id",
                principalTable: "work_projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_api_keys_work_projects_project_id",
                schema: "gateway",
                table: "api_keys");

            migrationBuilder.DropIndex(
                name: "IX_api_keys_project_id",
                schema: "gateway",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "Forced",
                table: "work_project_members");

            migrationBuilder.DropColumn(
                name: "project_id",
                schema: "gateway",
                table: "api_keys");
        }
    }
}
