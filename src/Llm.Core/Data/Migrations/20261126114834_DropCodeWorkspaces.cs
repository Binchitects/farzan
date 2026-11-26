using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropCodeWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_api_keys_studio_workspaces_workspace_id",
                schema: "gateway",
                table: "api_keys");

            migrationBuilder.DropTable(
                name: "studio_ports");

            migrationBuilder.DropTable(
                name: "studio_workspaces");

            migrationBuilder.DropIndex(
                name: "IX_api_keys_workspace_id",
                schema: "gateway",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "workspace_id",
                schema: "gateway",
                table: "api_keys");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "workspace_id",
                schema: "gateway",
                table: "api_keys",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "studio_workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Branch = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EngineKeyCipher = table.Column<string>(type: "text", nullable: true),
                    EnginePort = table.Column<int>(type: "integer", nullable: true),
                    EngineUrl = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    KeyAlias = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    KeyCipher = table.Column<string>(type: "text", nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Project = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_studio_workspaces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_studio_workspaces_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "studio_ports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Added = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Stopped = table.Column<bool>(type: "boolean", nullable: false),
                    Visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_studio_ports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_studio_ports_studio_workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "studio_workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_workspace_id",
                schema: "gateway",
                table: "api_keys",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "IX_studio_ports_WorkspaceId_Port",
                table: "studio_ports",
                columns: new[] { "WorkspaceId", "Port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_studio_workspaces_UserId_UsedAt",
                table: "studio_workspaces",
                columns: new[] { "UserId", "UsedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_api_keys_studio_workspaces_workspace_id",
                schema: "gateway",
                table: "api_keys",
                column: "workspace_id",
                principalTable: "studio_workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
