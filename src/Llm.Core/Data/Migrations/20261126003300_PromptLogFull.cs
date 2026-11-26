using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PromptLogFull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ChatId",
                table: "prompt_log",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "From",
                table: "prompt_log",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Thread",
                table: "prompt_log",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "prompt_log_bodies",
                columns: table => new
                {
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Request = table.Column<byte[]>(type: "bytea", nullable: false),
                    Response = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prompt_log_bodies", x => x.EntryId);
                    table.ForeignKey(
                        name: "FK_prompt_log_bodies_prompt_log_EntryId",
                        column: x => x.EntryId,
                        principalTable: "prompt_log",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_prompt_log_ChatId_At",
                table: "prompt_log",
                columns: new[] { "ChatId", "At" },
                filter: "\"ChatId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "prompt_log_bodies");

            migrationBuilder.DropIndex(
                name: "IX_prompt_log_ChatId_At",
                table: "prompt_log");

            migrationBuilder.DropColumn(
                name: "ChatId",
                table: "prompt_log");

            migrationBuilder.DropColumn(
                name: "From",
                table: "prompt_log");

            migrationBuilder.DropColumn(
                name: "Thread",
                table: "prompt_log");
        }
    }
}
