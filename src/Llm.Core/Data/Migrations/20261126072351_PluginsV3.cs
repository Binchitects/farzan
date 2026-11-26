using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class PluginsV3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plugin_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DataJson = table.Column<string>(type: "text", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Tries = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plugin_events_mcp_servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "mcp_servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plugin_jobs",
                columns: table => new
                {
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NextAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_jobs", x => new { x.ServerId, x.Name });
                    table.ForeignKey(
                        name: "FK_plugin_jobs_mcp_servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "mcp_servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plugin_managers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_managers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_plugin_managers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_plugin_managers_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_plugin_managers_mcp_servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "mcp_servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plugin_package_blobs",
                columns: table => new
                {
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_package_blobs", x => x.ReleaseId);
                    table.ForeignKey(
                        name: "FK_plugin_package_blobs_plugin_releases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "plugin_releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plugin_publishers",
                columns: table => new
                {
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PublicKeyPem = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    AddedById = table.Column<Guid>(type: "uuid", nullable: true),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_publishers", x => x.Fingerprint);
                    table.ForeignKey(
                        name: "FK_plugin_publishers_AspNetUsers_AddedById",
                        column: x => x.AddedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "plugin_states",
                columns: table => new
                {
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ChangedById = table.Column<Guid>(type: "uuid", nullable: true),
                    Signer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UntrustedSigner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DevOwnerId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_states", x => x.ServerId);
                    table.ForeignKey(
                        name: "FK_plugin_states_AspNetUsers_ChangedById",
                        column: x => x.ChangedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_plugin_states_AspNetUsers_DevOwnerId",
                        column: x => x.DevOwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_plugin_states_mcp_servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "mcp_servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plugin_store",
                columns: table => new
                {
                    ServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ValueJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plugin_store", x => new { x.ServerId, x.OwnerId, x.Key });
                    table.ForeignKey(
                        name: "FK_plugin_store_mcp_servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "mcp_servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_plugin_events_DueAt",
                table: "plugin_events",
                column: "DueAt");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_events_ServerId",
                table: "plugin_events",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_jobs_NextAt",
                table: "plugin_jobs",
                column: "NextAt");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_managers_GroupId",
                table: "plugin_managers",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_managers_ServerId_UserId_GroupId",
                table: "plugin_managers",
                columns: new[] { "ServerId", "UserId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_plugin_managers_UserId",
                table: "plugin_managers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_publishers_AddedById",
                table: "plugin_publishers",
                column: "AddedById");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_states_ChangedById",
                table: "plugin_states",
                column: "ChangedById");

            migrationBuilder.CreateIndex(
                name: "IX_plugin_states_DevOwnerId",
                table: "plugin_states",
                column: "DevOwnerId");

            // Plugins installed before 8.3 (plugins v2) become v3, as `arena-plugin migrate` makes a plugin's folder v3: the
            // apiVersion first, and a version like 1.2.3 (v2 took "1.0" or "2"). Their manifests and every kept version's.
            const string v3 = "'apiVersion: arena.plugins/v3' || E'\\n' || {0}";
            const string twoParts = "regexp_replace({0}, '(^|\\n)version:[ \\t]*[\"'']?(\\d+)\\.(\\d+)[\"'']?[ \\t]*(\\n|$)', '\\1version: \\2.\\3.0\\4')";
            const string onePart = "regexp_replace({0}, '(^|\\n)version:[ \\t]*[\"'']?(\\d+)[\"'']?[ \\t]*(\\n|$)', '\\1version: \\2.0.0\\3')";
            // Then a version still not like 1.2.3 (v2 took any) is 0.0.0, and a manifest with none gets it.
            const string odd = "regexp_replace({0}, '(^|\\n)version:(?![ \\t]*[\"'']?v?\\d+\\.\\d+\\.\\d+(-[0-9A-Za-z.-]+)?[\"'']?[ \\t]*(\\n|$))[^\\n]*', '\\1version: 0.0.0')";
            const string none = "CASE WHEN {0} ~ '(^|\\n)version:' THEN {0} ELSE {0} || E'\\nversion: 0.0.0' END";
            string F(string format, string arg) => string.Format(System.Globalization.CultureInfo.InvariantCulture, format, arg);
            string Fix(string column) => F(none, F(odd, F(onePart, F(twoParts,
                $"CASE WHEN {column} ~ '(^|\\n)apiVersion:' THEN {column} ELSE {F(v3, column)} END"))));
            migrationBuilder.Sql($"UPDATE mcp_servers SET \"Manifest\" = {Fix("\"Manifest\"")} WHERE \"Manifest\" IS NOT NULL;");
            var release = Fix("(\"FilesJson\"::jsonb->>'plugin.yaml')");
            migrationBuilder.Sql($$"""
                UPDATE plugin_releases SET "FilesJson" = jsonb_set("FilesJson"::jsonb, '{plugin.yaml}', to_jsonb({{release}}))::text
                WHERE "FilesJson"::jsonb ? 'plugin.yaml';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plugin_events");

            migrationBuilder.DropTable(
                name: "plugin_jobs");

            migrationBuilder.DropTable(
                name: "plugin_managers");

            migrationBuilder.DropTable(
                name: "plugin_package_blobs");

            migrationBuilder.DropTable(
                name: "plugin_publishers");

            migrationBuilder.DropTable(
                name: "plugin_states");

            migrationBuilder.DropTable(
                name: "plugin_store");
        }
    }
}
