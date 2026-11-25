using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class CanvasSlides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DeckId",
                table: "canvases",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_canvases_DeckId",
                table: "canvases",
                column: "DeckId");

            migrationBuilder.AddForeignKey(
                name: "FK_canvases_decks_DeckId",
                table: "canvases",
                column: "DeckId",
                principalTable: "decks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // The Decks tool is now the canvas's slides: wherever "decks" was chosen (chats, assistants, scheduled tasks and
            // their templates, toolsets), "canvas" is, once; its settings and its groups' are gone with it.
            foreach (var (table, column) in new[]
            {
                ("conversations", "Tools"), ("conversations", "LoadedTools"), ("projects", "Tools"), ("scheduled_tasks", "Tools"), ("task_templates", "Tools"), ("toolsets", "Tools"),
            })
            {
                migrationBuilder.Sql($$"""
                    update "{{table}}" set "{{column}}" = array_remove("{{column}}", 'decks') || case when 'canvas' = any("{{column}}") then '{}'::text[] else '{canvas}'::text[] end
                    where 'decks' = any("{{column}}");
                    """);
            }
            migrationBuilder.Sql("""delete from "tool_group_settings" where "ToolId" = 'decks';""");
            migrationBuilder.Sql("""delete from "access_rules" where "ResourceKind" = 'tool' and "Resource" = 'decks';""");
            migrationBuilder.Sql("""delete from "tool_settings" where "ToolId" = 'decks';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_canvases_decks_DeckId",
                table: "canvases");

            migrationBuilder.DropIndex(
                name: "IX_canvases_DeckId",
                table: "canvases");

            migrationBuilder.DropColumn(
                name: "DeckId",
                table: "canvases");
        }
    }
}
