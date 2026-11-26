using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <summary>
    /// gateway.requests_hourly: the ledger summed by hour (UTC) and by person, model, source, kind, key, state and
    /// project, for the dashboards' sums and counts over long windows (SqlMacros' $__requests). Kept exact by triggers on
    /// the ledger, a statement at a time: what is added, changed (the old taken away, the new added) or deleted, and
    /// emptied with it. Made from the ledger as it is.
    /// </summary>
    public partial class RequestsHourly : Migration
    {
        /// <summary>The sums of a set of ledger rows (a transition table) by its group, times a sign; ordered so concurrent statements lock alike.</summary>
        private static string Sums(string rows, string sign) => $"""
            select date_trunc('hour', r.started_at, 'UTC'), r.person, r.model, r.surface, r.credit_kind, r.key_alias, r.status, r.project_id,
                   {sign} count(*), {sign} sum(r.prompt_tokens), {sign} sum(r.cached_tokens), {sign} sum(r.completion_tokens), {sign} sum(r.total_tokens),
                   {sign} sum(r.cost), {sign} sum(r.duration_ms)
            from {rows} r
            group by 1, 2, 3, 4, 5, 6, 7, 8
            order by 1, 2, 3, 4, 5, 6, 7, 8
            """;

        private static string Upsert(string rows, string sign) => $"""
            insert into gateway.requests_hourly as h (hour, person, model, surface, credit_kind, key_alias, status, project_id,
              requests, prompt_tokens, cached_tokens, completion_tokens, total_tokens, cost, duration_ms)
            {Sums(rows, sign)}
            on conflict (hour, person, model, surface, credit_kind, key_alias, status, project_id) do update set
              requests = h.requests + excluded.requests, prompt_tokens = h.prompt_tokens + excluded.prompt_tokens,
              cached_tokens = h.cached_tokens + excluded.cached_tokens, completion_tokens = h.completion_tokens + excluded.completion_tokens,
              total_tokens = h.total_tokens + excluded.total_tokens, cost = h.cost + excluded.cost, duration_ms = h.duration_ms + excluded.duration_ms;
            """;

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                create table gateway.requests_hourly (
                  hour timestamptz not null,
                  person varchar(256),
                  model varchar(200) not null,
                  surface varchar(12) not null,
                  credit_kind varchar(12) not null,
                  key_alias varchar(120),
                  status varchar(10) not null,
                  project_id uuid,
                  requests bigint not null,
                  prompt_tokens bigint not null,
                  cached_tokens bigint not null,
                  completion_tokens bigint not null,
                  total_tokens bigint not null,
                  cost numeric(24,9) not null,
                  duration_ms bigint not null
                );
                create unique index "IX_requests_hourly_group" on gateway.requests_hourly (hour, person, model, surface, credit_kind, key_alias, status, project_id) nulls not distinct;
                """);
            migrationBuilder.Sql($"""
                {Upsert("gateway.requests", "")}

                create function gateway.requests_hourly_added() returns trigger language plpgsql as $$
                begin
                  {Upsert("added", "")}
                  return null;
                end $$;

                create function gateway.requests_hourly_changed() returns trigger language plpgsql as $$
                begin
                  {Upsert("before_change", "-")}
                  {Upsert("after_change", "")}
                  delete from gateway.requests_hourly where requests = 0 and hour in (select date_trunc('hour', started_at, 'UTC') from before_change);
                  return null;
                end $$;

                create function gateway.requests_hourly_deleted() returns trigger language plpgsql as $$
                begin
                  {Upsert("deleted", "-")}
                  delete from gateway.requests_hourly where requests = 0 and hour in (select date_trunc('hour', started_at, 'UTC') from deleted);
                  return null;
                end $$;

                create function gateway.requests_hourly_emptied() returns trigger language plpgsql as $$
                begin
                  truncate gateway.requests_hourly;
                  return null;
                end $$;

                create trigger requests_hourly_added after insert on gateway.requests referencing new table as added
                  for each statement execute function gateway.requests_hourly_added();
                create trigger requests_hourly_changed after update on gateway.requests referencing old table as before_change new table as after_change
                  for each statement execute function gateway.requests_hourly_changed();
                create trigger requests_hourly_deleted after delete on gateway.requests referencing old table as deleted
                  for each statement execute function gateway.requests_hourly_deleted();
                create trigger requests_hourly_emptied after truncate on gateway.requests
                  for each statement execute function gateway.requests_hourly_emptied();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                drop trigger if exists requests_hourly_added on gateway.requests;
                drop trigger if exists requests_hourly_changed on gateway.requests;
                drop trigger if exists requests_hourly_deleted on gateway.requests;
                drop trigger if exists requests_hourly_emptied on gateway.requests;
                drop function if exists gateway.requests_hourly_added();
                drop function if exists gateway.requests_hourly_changed();
                drop function if exists gateway.requests_hourly_deleted();
                drop function if exists gateway.requests_hourly_emptied();
                drop table if exists gateway.requests_hourly;
                """);
        }
    }
}
