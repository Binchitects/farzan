# A million rows: exports, reports and the prompt log (v8.2.3)

Measured on 2026-11-26 with `MillionRowMeasurements` (`tools/dn test tests/Llm.Tests -c Release --filter MillionRow -e ARENA_MEASURE_MILLION=1`):
a million requests in the ledger and a million prompt log lines over the last 30 days, seeded in the test's own Postgres
(200 people, each with their own source, kind and project and one or two of six models), vacuumed and analysed. Each asked
twice, cold then warm; the target is under 2 s warm. No Prometheus or Loki: their panels answer at once, the ledger's are
measured. The host is the live stack's, and was busy with other test runs: numbers move by a few hundred ms between runs.

| What | Cold | Warm | Under 2 s |
|---|---|---|---|
| Accounting, 30 days | 1,678 ms | 1,476 ms | yes |
| Accounting, CSV | 1,383 ms | 1,364 ms | yes |
| Accounting, Excel | 1,302 ms | 1,408 ms | yes |
| Accounting, PDF | 1,322 ms | 1,322 ms | yes |
| Accounting, by tokens | 1,440 ms | 1,457 ms | yes |
| Accounting, one model | 1,241 ms | 1,302 ms | yes |
| Accounting, one person | 1,119 ms | 1,167 ms | yes |
| Dashboard code-intel, CSV | 28 ms | 12 ms | yes |
| Dashboard code-intel, PDF | 10 ms | 9 ms | yes |
| Dashboard gpu-hardware, CSV | 18 ms | 8 ms | yes |
| Dashboard gpu-hardware, PDF | 11 ms | 13 ms | yes |
| Dashboard hardware, CSV | 1,144 ms | 1,165 ms | yes |
| Dashboard hardware, PDF | 1,120 ms | 1,127 ms | yes |
| Dashboard host-containers, CSV | 9 ms | 14 ms | yes |
| Dashboard host-containers, PDF | 7 ms | 7 ms | yes |
| Dashboard llm-overview, CSV | 564 ms | 536 ms | yes |
| Dashboard llm-overview, PDF | 513 ms | 528 ms | yes |
| Dashboard llm-users, CSV | 820 ms | 810 ms | yes |
| Dashboard llm-users, PDF | 813 ms | 788 ms | yes |
| Dashboard resources, CSV | 11 ms | 8 ms | yes |
| Dashboard resources, PDF | 10 ms | 7 ms | yes |
| Dashboard stack-health, CSV | 6 ms | 9 ms | yes |
| Dashboard stack-health, PDF | 6 ms | 7 ms | yes |
| Dashboard stack-indexing, CSV | 10 ms | 9 ms | yes |
| Dashboard stack-indexing, PDF | 9 ms | 11 ms | yes |
| Dashboard stack-logs, CSV | 8 ms | 13 ms | yes |
| Dashboard stack-logs, PDF | 7 ms | 10 ms | yes |
| Dashboard stack-performance, CSV | 1,313 ms | 1,272 ms | yes |
| Dashboard stack-performance, PDF | 1,264 ms | 1,266 ms | yes |
| Dashboard system, CSV | 7 ms | 6 ms | yes |
| Dashboard system, PDF | 8 ms | 7 ms | yes |
| Dashboard usage-by-user, CSV | 1,021 ms | 1,068 ms | yes |
| Dashboard usage-by-user, PDF | 1,007 ms | 1,115 ms | yes |
| Prompt log, a model | 7 ms | 4 ms | yes |
| Prompt log, a person | 11 ms | 7 ms | yes |
| Prompt log, a tool called | 8 ms | 3 ms | yes |
| Prompt log, failed | 9 ms | 5 ms | yes |
| Prompt log, newest | 45 ms | 6 ms | yes |
| Prompt log, words | 10 ms | 6 ms | yes |
| Prompt log, words and a model, 30 days | 9 ms | 5 ms | yes |
| Usage by person, CSV | 319 ms | 302 ms | yes |
| Usage by person, one model, Excel | 102 ms | 93 ms | yes |

## What made the exports faster

1. A dashboard's export asked its panels one after another; it asks up to eight at once (each its own connection), as its
   page does.
2. The ledger by hour, `gateway.requests_hourly`, kept by statement triggers on `gateway.requests`: the 38 panels that sum
   or count it read `$__requests`, whole hours from the rollup and the minutes either side from the rows, so every number
   is the rows' own (`LedgerRollupTests` runs each of them both ways over windows that start and end inside an hour, and
   fails when the edges are left out); the usage exports read it too. Single requests stay on the rows.
3. Their times as a sketch by hour, `gateway.requests_hourly_spread` (each request's time and its time to the first token,
   counted in buckets of a log scale 2% wide, by model, refused or not, media or not), kept by the same kind of triggers:
   the 12 percentile panels read `$__spread` and `gateway.sketch_quantile`, which ranks as `percentile_disc` and is within
   1% of the rows' own (`LedgerRollupTests` checks every quantile by model and by hour against the rows). Stack
   Performance's "Tokens generated, by model" reads the rows once for all models (its busiest minute and decode speed),
   not once a model.

| Export (CSV, warm) | One after another | Eight at once | With the rollup | With the sketch |
|---|---|---|---|---|
| hardware | 3,936 ms | 3,091 ms | 2,885 ms | 1,165 ms |
| llm-overview | 3,980 ms | 1,569 ms | 1,320 ms | 536 ms |
| llm-users | 6,763 ms | 2,387 ms | 1,678 ms | 810 ms |
| stack-performance | 3,252 ms | 1,814 ms | 1,877 ms | 1,272 ms |
| usage-by-user | 6,826 ms | 3,160 ms | 1,094 ms | 1,068 ms |
| Usage by person | 1,421 ms | 1,268 ms | 344 ms | 302 ms |

(The first two columns were measured with a seed whose every request had its own mix of model, source and project, which
no rollup shrinks; the last two with people working as people do. All seeds are a million rows over 30 days.)

The prompt log answers in milliseconds on a million lines (its newest page, words by trigram index, a model, a tool, a
person, a state), and Accounting (whole, by tokens, narrowed to a model or a person, its PDF, Excel and CSV) in 1.2 to
1.5 s.
