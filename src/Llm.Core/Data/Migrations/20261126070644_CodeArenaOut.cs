using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class CodeArenaOut : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Code's workspaces left with Code Arena (its own project now): the permission to open them goes from roles and
            // rules. The workspaces' rows stay as they were (people's data, kept until an admin says otherwise).
            migrationBuilder.Sql("""
                UPDATE access_roles SET "Permissions" = array_remove("Permissions", 'code.studio') WHERE 'code.studio' = ANY("Permissions");
                DELETE FROM access_rules WHERE "Resource" = 'code.studio';
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
