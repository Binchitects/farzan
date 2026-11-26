using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class TasksIntoAutomations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "automation_templates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Plugin = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Prompt = table.Column<string>(type: "text", nullable: false),
                    Cron = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Tools = table.Column<List<string>>(type: "text[]", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_templates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_automation_templates_mcp_servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "mcp_servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_automation_templates_Plugin_Name",
                table: "automation_templates",
                columns: new[] { "Plugin", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_automation_templates_ServerId",
                table: "automation_templates",
                column: "ServerId");
            // Plugins' scheduled tasks: offered as Automations' templates.
            migrationBuilder.Sql("""
                INSERT INTO automation_templates ("Id", "ServerId", "Plugin", "Name", "Description", "Prompt", "Cron", "Tools", "UpdatedAt")
                SELECT "Id", "ServerId", "Plugin", "Name", "Description", "Prompt", "Cron", "Tools", "UpdatedAt" FROM task_templates;
                """);

            // Each scheduled task becomes the workflow of the same id (so its old webhook address, its chats and Code Arena's
            // ids still find it): its trigger; its question as an agent step (its model, thinking, tools, one chat carried on) or
            // its review as a review step; then what it delivered: the bell always, email, its webhook (the address stays
            // encrypted with APP_DATA_KEY, as it was), its GitLab comment.
            migrationBuilder.Sql("""
                INSERT INTO workflows ("Id", "OwnerId", "Name", "Description", "Flow", "Enabled", "Reach", "GroupIds", "SecretHash", "NextRunAt", "LastRunAt", "CreatedAt", "UpdatedAt")
                SELECT t."Id", t."UserId", t."Name", 'Was a scheduled task.',
                    jsonb_build_object(
                        'trigger', CASE t."Trigger"
                            WHEN 'gitlab' THEN jsonb_build_object('kind', 'gitlab', 'events', to_jsonb(t."Events"))
                            WHEN 'webhook' THEN jsonb_build_object('kind', 'webhook')
                            ELSE jsonb_build_object('kind', 'schedule', 'cron', t."Cron", 'timeZone', t."TimeZone") END,
                        'steps',
                            jsonb_build_array(CASE WHEN t."Action" = 'ask' THEN jsonb_strip_nulls(jsonb_build_object(
                                'id', 'answer', 'kind', 'agent', 'name', 'Answer',
                                'prompt', t."Prompt" || CASE WHEN t."Trigger" = 'schedule' THEN '' ELSE E'\n\n{{input.text}}' END,
                                'tools', to_jsonb(t."Tools"), 'model', t."Model", 'thinking', t."Thinking",
                                'sameChat', CASE WHEN t."SameChat" THEN true END))
                            ELSE jsonb_strip_nulls(jsonb_build_object(
                                'id', 'answer', 'kind', 'review', 'name', 'Review', 'review', t."Action",
                                'repositories', array_to_string(t."Repositories", E'\n'), 'ref', t."Ref", 'cross', t."Cross")) END)
                            || jsonb_build_array(jsonb_build_object('id', 'tell', 'kind', 'notify', 'name', 'Tell me', 'subject', t."Name", 'text', '{{answer.text}}'))
                            || CASE WHEN t."Email" THEN jsonb_build_array(jsonb_build_object('id', 'mail', 'kind', 'email', 'name', 'Email it', 'subject', t."Name", 'text', '{{answer.text}}')) ELSE '[]'::jsonb END
                            || CASE WHEN t."WebhookEncrypted" IS NOT NULL THEN jsonb_build_array(jsonb_build_object('id', 'post', 'kind', 'webhook', 'name', 'Post it', 'text', t."Name" || E'\n\n{{answer.text}}', 'secret', t."WebhookEncrypted")) ELSE '[]'::jsonb END
                            || CASE WHEN t."ReplyInGitLab" THEN jsonb_build_array(jsonb_build_object('id', 'reply', 'kind', 'comment', 'name', 'Answer in GitLab', 'text', '{{answer.text}}')) ELSE '[]'::jsonb END
                    )::text,
                    t."Enabled", 'private', '{}'::uuid[], t."TriggerSecretHash", CASE WHEN t."Trigger" = 'schedule' THEN t."NextRunAt" END, t."LastRunAt", t."CreatedAt", t."UpdatedAt"
                FROM scheduled_tasks t;
                """);

            // Their runs, as the workflow's: by hand or by their trigger; one step each, with the chat or review it made and what it delivered.
            // A run going while Arena was upgraded is ended, saying so; a skipped one was stopped.
            migrationBuilder.Sql("""
                INSERT INTO workflow_runs ("Id", "WorkflowId", "Flow", "Trigger", "ById", "Input", "Status", "Error", "StartedAt", "FinishedAt")
                SELECT r."Id", r."TaskId", w."Flow", CASE WHEN r."Manual" THEN 'manual' ELSE t."Trigger" END, CASE WHEN r."Manual" THEN t."UserId" END, '{"text":""}',
                    CASE r."Status" WHEN 'running' THEN 'failed' WHEN 'skipped' THEN 'stopped' ELSE r."Status" END,
                    CASE WHEN r."Status" = 'running' THEN 'It stopped: Arena was upgraded while it ran.' ELSE r."Error" END,
                    r."StartedAt", COALESCE(r."FinishedAt", CASE WHEN r."Status" = 'running' THEN now() END)
                FROM scheduled_runs r JOIN scheduled_tasks t ON t."Id" = r."TaskId" JOIN workflows w ON w."Id" = r."TaskId";
                INSERT INTO workflow_run_steps ("Id", "RunId", "Order", "Path", "StepId", "Kind", "Name", "Status", "Input", "Output", "Link", "Error", "StartedAt", "FinishedAt")
                SELECT gen_random_uuid(), r."Id", 1, 'answer', 'answer', CASE WHEN t."Action" = 'ask' THEN 'agent' ELSE 'review' END, CASE WHEN t."Action" = 'ask' THEN 'Answer' ELSE 'Review' END,
                    CASE r."Status" WHEN 'running' THEN 'failed' ELSE r."Status" END, t."Prompt", COALESCE(r."Delivery", ''),
                    CASE WHEN r."ConversationId" IS NOT NULL THEN '/chat/' || r."ConversationId" ELSE r."Link" END, r."Error", r."StartedAt", r."FinishedAt"
                FROM scheduled_runs r JOIN scheduled_tasks t ON t."Id" = r."TaskId";
                """);

            // Scheduling was its own permission; it is Automations' now. A role that held it holds Automations; whoever was
            // denied it is denied Automations (no one gets more than they had). Everyone's tasks (tasks.manage.all) is gone with them.
            migrationBuilder.Sql("""
                UPDATE access_roles SET "Permissions" = array_remove(array_remove("Permissions", 'tasks.manage.all'), 'feature.tasks')
                    || CASE WHEN 'feature.tasks' = ANY("Permissions") AND NOT 'feature.studio' = ANY("Permissions") THEN ARRAY['feature.studio'] ELSE '{}'::text[] END
                WHERE 'feature.tasks' = ANY("Permissions") OR 'tasks.manage.all' = ANY("Permissions");
                DELETE FROM access_rules r WHERE r."Resource" = 'feature.tasks' AND EXISTS (
                    SELECT 1 FROM access_rules s WHERE s."Resource" = 'feature.studio' AND s."ResourceKind" = r."ResourceKind" AND s."Action" = r."Action"
                        AND s."Effect" = r."Effect" AND s."SubjectKind" = r."SubjectKind" AND s."SubjectId" IS NOT DISTINCT FROM r."SubjectId");
                UPDATE access_rules SET "Resource" = 'feature.studio' WHERE "Resource" = 'feature.tasks';
                DELETE FROM access_rules WHERE "Resource" = 'tasks.manage.all';
                """);

            migrationBuilder.DropTable(
                name: "scheduled_runs");

            migrationBuilder.DropTable(
                name: "task_events");

            migrationBuilder.DropTable(
                name: "task_templates");

            migrationBuilder.DropTable(
                name: "scheduled_tasks");

            migrationBuilder.RenameColumn(
                name: "ScheduledTaskId",
                table: "conversations",
                newName: "WorkflowId");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_templates");

            migrationBuilder.RenameColumn(
                name: "WorkflowId",
                table: "conversations",
                newName: "ScheduledTaskId");

            migrationBuilder.CreateTable(
                name: "scheduled_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "ask"),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Cron = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Cross = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Email = table.Column<bool>(type: "boolean", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Events = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Prompt = table.Column<string>(type: "text", nullable: false),
                    Ref = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ReplyInGitLab = table.Column<bool>(type: "boolean", nullable: false),
                    Repositories = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    RunningOn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RunningSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SameChat = table.Column<bool>(type: "boolean", nullable: false),
                    Thinking = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TimeZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Tools = table.Column<List<string>>(type: "text[]", nullable: true),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "schedule"),
                    TriggerSecretHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WebhookEncrypted = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scheduled_tasks_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_templates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Cron = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Plugin = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Prompt = table.Column<string>(type: "text", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tools = table.Column<List<string>>(type: "text[]", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_templates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_templates_mcp_servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "mcp_servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Delivery = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Link = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Manual = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scheduled_runs_scheduled_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "scheduled_tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReplyId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ReplyKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ReplyProject = table.Column<long>(type: "bigint", nullable: true),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_events_scheduled_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "scheduled_tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_runs_TaskId_StartedAt",
                table: "scheduled_runs",
                columns: new[] { "TaskId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_tasks_NextRunAt",
                table: "scheduled_tasks",
                column: "NextRunAt");

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_tasks_UserId",
                table: "scheduled_tasks",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_task_events_TaskId_CreatedAt",
                table: "task_events",
                columns: new[] { "TaskId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_task_templates_Plugin_Name",
                table: "task_templates",
                columns: new[] { "Plugin", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_task_templates_ServerId",
                table: "task_templates",
                column: "ServerId");
        }
    }
}
