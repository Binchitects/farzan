using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <summary>
    /// gateway.requests_hourly_spread: how long the ledger's requests took, and how long to their first token, as a sketch
    /// by hour (UTC), model, refused or not, and media or not: each value counted in a bucket of a log scale 2% wide, so a
    /// percentile read from it (gateway.sketch_quantile, the rank percentile_disc takes) is within 1% of the requests' own.
    /// Kept by triggers on the ledger, as gateway.requests_hourly is; made from the ledger as it is.
    /// </summary>
    public partial class RequestsSpread : Migration
    {
        /// <summary>A set of ledger rows (a transition table) as the sketch's counts, times a sign; ordered so concurrent statements lock alike.</summary>
        private static string Counts(string rows, string sign) => $"""
            select date_trunc('hour', v.started_at, 'UTC'), v.model, v.refused, v.media, v.metric, gateway.sketch_bucket(v.ms), {sign} count(*)
            from (select r.started_at, r.model, r.status = 'refused' as refused, r.surface = 'media' as media, 'd'::char(1) as metric, r.duration_ms::float8 as ms from {rows} r
                  union all
                  select r.started_at, r.model, r.status = 'refused', r.surface = 'media', 'f'::char(1), extract(epoch from (r.first_token_at - r.started_at)) * 1000
                  from {rows} r where r.first_token_at is not null) v
            group by 1, 2, 3, 4, 5, 6
            order by 1, 2, 3, 4, 5, 6
            """;

        private static string Upsert(string rows, string sign) => $"""
            insert into gateway.requests_hourly_spread as h (hour, model, refused, media, metric, bucket, n)
            {Counts(rows, sign)}
            on conflict (hour, model, refused, media, metric, bucket) do update set n = h.n + excluded.n;
            """;

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- A value's bucket on a log scale 2% wide (DDSketch): 1.02^(b-1) < ms <= 1.02^b; nothing or less at -2000000.
                create function gateway.sketch_bucket(ms float8) returns int language sql immutable parallel safe as $$
                  select case when ms is null then null when ms <= 0 then -2000000 else ceil(ln(ms) / ln(1.02))::int end
                $$;
                -- A bucket's value: the one within 1% of all it holds.
                create function gateway.sketch_value(b int) returns float8 language sql immutable parallel safe as $$
                  select case when b = -2000000 then 0 else 2 * power(1.02::float8, b) / 2.02 end
                $$;
                -- The p-th quantile of counts by bucket (buckets may repeat): the first bucket whose running count reaches p of
                -- the whole, as percentile_disc ranks; null with none.
                create function gateway.sketch_quantile(p float8, buckets int[], counts bigint[]) returns float8 language sql immutable parallel safe as $$
                  select gateway.sketch_value(b) from (
                    select b, sum(c) over (order by b) as running, sum(c) over () as whole
                    from (select b, sum(c) as c from unnest(buckets, counts) u(b, c) group by b having sum(c) > 0) x
                  ) y where running >= p * whole order by b limit 1
                $$;
                create table gateway.requests_hourly_spread (
                  hour timestamptz not null,
                  model varchar(200) not null,
                  refused boolean not null,
                  media boolean not null,
                  metric char(1) not null,
                  bucket int not null,
                  n bigint not null,
                  primary key (hour, model, refused, media, metric, bucket)
                );
                """);
            migrationBuilder.Sql($"""
                {Upsert("gateway.requests", "")}

                create function gateway.requests_spread_added() returns trigger language plpgsql as $$
                begin
                  {Upsert("added", "")}
                  return null;
                end $$;

                create function gateway.requests_spread_changed() returns trigger language plpgsql as $$
                begin
                  {Upsert("before_change", "-")}
                  {Upsert("after_change", "")}
                  delete from gateway.requests_hourly_spread where n = 0 and hour in (select date_trunc('hour', started_at, 'UTC') from before_change);
                  return null;
                end $$;

                create function gateway.requests_spread_deleted() returns trigger language plpgsql as $$
                begin
                  {Upsert("deleted", "-")}
                  delete from gateway.requests_hourly_spread where n = 0 and hour in (select date_trunc('hour', started_at, 'UTC') from deleted);
                  return null;
                end $$;

                create function gateway.requests_spread_emptied() returns trigger language plpgsql as $$
                begin
                  truncate gateway.requests_hourly_spread;
                  return null;
                end $$;

                create trigger requests_spread_added after insert on gateway.requests referencing new table as added
                  for each statement execute function gateway.requests_spread_added();
                create trigger requests_spread_changed after update on gateway.requests referencing old table as before_change new table as after_change
                  for each statement execute function gateway.requests_spread_changed();
                create trigger requests_spread_deleted after delete on gateway.requests referencing old table as deleted
                  for each statement execute function gateway.requests_spread_deleted();
                create trigger requests_spread_emptied after truncate on gateway.requests
                  for each statement execute function gateway.requests_spread_emptied();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                drop trigger if exists requests_spread_added on gateway.requests;
                drop trigger if exists requests_spread_changed on gateway.requests;
                drop trigger if exists requests_spread_deleted on gateway.requests;
                drop trigger if exists requests_spread_emptied on gateway.requests;
                drop function if exists gateway.requests_spread_added();
                drop function if exists gateway.requests_spread_changed();
                drop function if exists gateway.requests_spread_deleted();
                drop function if exists gateway.requests_spread_emptied();
                drop table if exists gateway.requests_hourly_spread;
                drop function if exists gateway.sketch_quantile(float8, int[], bigint[]);
                drop function if exists gateway.sketch_value(int);
                drop function if exists gateway.sketch_bucket(float8);
                """);
        }
    }
}
