using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LedgerFirstTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Requests imported from LiteLLM whose first token was its end (an answer not streamed): no first token is
            // known, so no time to first token and no decode speed are read from them (they read 54,000 tokens a second).
            migrationBuilder.Sql("""
                update gateway.requests set first_token_at = null
                where source_id is not null and first_token_at is not null and first_token_at >= ended_at - interval '50 milliseconds';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
