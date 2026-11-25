using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LedgerChats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Chat answers logged before requests carried their chat (and so their project): each found by its asker, model,
            // prompt and answer tokens, started within seconds of the answer; linked to its chat, and to the chat's project,
            // so a project counts what its chats used whenever they were asked (810 of 3,830 on the live stack, 2026-11-26).
            migrationBuilder.Sql("""
                with m as (
                  select m."Id" as message, m."ConversationId" as conv, c."WorkProjectId" as proj, lower(u."Email") as email, m."Model" as model,
                         m."PromptTokens" as pt, m."CompletionTokens" as ct, m."CreatedAt" as at
                  from chat_messages m join conversations c on c."Id" = m."ConversationId" join "AspNetUsers" u on u."Id" = c."UserId"
                  where m."Role" = 'assistant' and m."CompletionTokens" is not null and m."PromptTokens" is not null),
                pair as (
                  select distinct on (r.id) r.id as rid, m.conv, m.proj
                  from gateway.requests r join m on lower(r.person) = m.email and r.model = m.model and r.prompt_tokens = m.pt and r.completion_tokens = m.ct
                    and r.started_at between m.at - interval '2 seconds' and m.at + interval '10 seconds'
                  where r.conversation_id is null and r.surface = 'chat'
                  order by r.id, abs(extract(epoch from r.started_at - m.at)))
                update gateway.requests r set conversation_id = pair.conv, project_id = coalesce(pair.proj, r.project_id)
                from pair where r.id = pair.rid;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
