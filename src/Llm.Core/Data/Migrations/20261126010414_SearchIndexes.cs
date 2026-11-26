using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Words found anywhere in a question, an answer or an audit entry by an index, not a read of every row
            // (PostgreSQL's own pg_trgm, trusted: the database's owner may add it). Where it may not be added, the searches
            // still work, reading every row as before.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                  BEGIN
                    CREATE EXTENSION IF NOT EXISTS pg_trgm;
                  EXCEPTION WHEN insufficient_privilege OR undefined_file THEN
                    RAISE NOTICE 'pg_trgm could not be added: searches read every row';
                  END;
                  IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm') THEN
                    CREATE INDEX IF NOT EXISTS "IX_prompt_log_Prompt_trgm" ON prompt_log USING gin ("Prompt" gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS "IX_prompt_log_Answer_trgm" ON prompt_log USING gin ("Answer" gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS "IX_chat_messages_Content_trgm" ON chat_messages USING gin ("Content" gin_trgm_ops) WHERE "Role" = 'user';
                    CREATE INDEX IF NOT EXISTS "IX_audit_events_Detail_trgm" ON audit_events USING gin ("Detail" gin_trgm_ops);
                  END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_prompt_log_Prompt_trgm";
                DROP INDEX IF EXISTS "IX_prompt_log_Answer_trgm";
                DROP INDEX IF EXISTS "IX_chat_messages_Content_trgm";
                DROP INDEX IF EXISTS "IX_audit_events_Detail_trgm";
                """);
        }
    }
}
