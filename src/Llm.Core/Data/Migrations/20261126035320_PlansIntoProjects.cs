using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlansIntoProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<Guid>>(
                name: "DependsOn",
                table: "work_project_tasks",
                type: "uuid[]",
                nullable: false,
                defaultValueSql: "'{}'::uuid[]");

            migrationBuilder.AddColumn<DateOnly>(
                name: "DueOn",
                table: "work_project_tasks",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "work_project_tasks",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Owner",
                table: "work_project_tasks",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "StartOn",
                table: "work_project_tasks",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "work_project_tasks",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "todo");

            // A task done before it had a status is done.
            migrationBuilder.Sql("""UPDATE work_project_tasks SET "Status" = 'done' WHERE "Done";""");

            // Each chat's plan in a project becomes the project's goal of the same id (the chat's plan card finds it), after its
            // goals: done when every step is, doing when any began, else planned; due when its last step is. Done then, not now:
            // what the project did in a time stays as it was.
            migrationBuilder.Sql("""
                INSERT INTO work_project_goals ("Id", "ProjectId", "Title", "Description", "Status", "DueOn", "DoneAt", "Position", "ChatId", "CreatedAt")
                SELECT p."Id", c."WorkProjectId", left(coalesce(nullif(p."Title", ''), 'A plan'), 200), left(p."Goal", 2000),
                    CASE WHEN s.n > 0 AND s.done = s.n THEN 'done' WHEN s.begun > 0 THEN 'doing' ELSE 'planned' END,
                    s.due, CASE WHEN s.n > 0 AND s.done = s.n THEN p."UpdatedAt" END,
                    coalesce((SELECT max(g."Position") FROM work_project_goals g WHERE g."ProjectId" = c."WorkProjectId"), -1)
                        + row_number() OVER (PARTITION BY c."WorkProjectId" ORDER BY p."CreatedAt"),
                    c."Id", p."CreatedAt"
                FROM plans p
                JOIN conversations c ON c."Id" = p."ConversationId"
                CROSS JOIN LATERAL (
                    SELECT count(*) AS n, count(*) FILTER (WHERE x."Status" = 'done') AS done, count(*) FILTER (WHERE x."Status" IN ('doing', 'done')) AS begun, max(x."Due") AS due
                    FROM plan_steps x WHERE x."PlanId" = p."Id") s
                WHERE p."Source" = 'chat' AND c."WorkProjectId" IS NOT NULL;
                """);

            // Its steps are the goal's tasks, of the same ids: where each stands, who does it, when, what it waits for (its
            // steps' numbers as their tasks), done when it was checked or the plan last changed.
            migrationBuilder.Sql("""
                INSERT INTO work_project_tasks ("Id", "ProjectId", "GoalId", "Title", "Notes", "Owner", "Status", "Done", "DoneAt", "StartOn", "DueOn", "DependsOn", "CreatedById", "ChatId", "CreatedAt")
                SELECT st."Id", c."WorkProjectId", p."Id", left(st."Title", 300), left(st."Notes", 4000), st."Owner", st."Status", st."Status" = 'done',
                    CASE WHEN st."Status" = 'done' THEN coalesce(st."CheckedAt", p."UpdatedAt") END, st."Start", st."Due",
                    coalesce(ARRAY(SELECT d."Id" FROM plan_steps d WHERE d."PlanId" = st."PlanId" AND d."Number" = ANY(st."DependsOn") ORDER BY d."Number"), '{}'::uuid[]),
                    p."UserId", c."Id", p."CreatedAt"
                FROM plan_steps st
                JOIN plans p ON p."Id" = st."PlanId"
                JOIN conversations c ON c."Id" = p."ConversationId"
                WHERE p."Source" = 'chat' AND c."WorkProjectId" IS NOT NULL;
                DELETE FROM plan_steps st USING plans p, conversations c
                    WHERE p."Id" = st."PlanId" AND c."Id" = p."ConversationId" AND p."Source" = 'chat' AND c."WorkProjectId" IS NOT NULL;
                DELETE FROM plans p USING conversations c
                    WHERE c."Id" = p."ConversationId" AND p."Source" = 'chat' AND c."WorkProjectId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DependsOn",
                table: "work_project_tasks");

            migrationBuilder.DropColumn(
                name: "DueOn",
                table: "work_project_tasks");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "work_project_tasks");

            migrationBuilder.DropColumn(
                name: "Owner",
                table: "work_project_tasks");

            migrationBuilder.DropColumn(
                name: "StartOn",
                table: "work_project_tasks");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "work_project_tasks");
        }
    }
}
