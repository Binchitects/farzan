# A million rows: exports, reports and the prompt log (v8.2.3)

Measured on 2026-11-26 with `MillionRowMeasurements` (`tools/dn test tests/Llm.Tests -c Release --filter MillionRow -e ARENA_MEASURE_MILLION=1`):
a million requests in the ledger and a million prompt log lines over the last 30 days, seeded in the test's own Postgres
(200 people, each with their own source, kind and project and one or two of six models), vacuumed and analysed. Each asked
twice, cold then warm; the target is under 2 s warm. No Prometheus or Loki: their panels answer at once, the ledger's are
measured. The host is the live stack's, and was busy with other test runs: numbers move by a few hundred ms between runs.

| What | Cold | Warm | Under 2 s |
|---|---|---|---|
| Accounting, 30 days | 1,781 ms | 1,679 ms | yes |
| Accounting, CSV | 1,508 ms | 1,501 ms | yes |
| Accounting, Excel | 1,529 ms | 1,487 ms | yes |
| Accounting, PDF | 1,461 ms | 1,525 ms | yes |
| Accounting, by tokens | 1,504 ms | 1,506 ms | yes |
| Accounting, one model | 1,402 ms | 1,340 ms | yes |
| Accounting, one person | 1,255 ms | 1,267 ms | yes |
| Dashboard code-intel, CSV | 31 ms | 10 ms | yes |
| Dashboard code-intel, PDF | 12 ms | 18 ms | yes |
| Dashboard gpu-hardware, CSV | 19 ms | 23 ms | yes |
| Dashboard gpu-hardware, PDF | 15 ms | 15 ms | yes |
| Dashboard hardware, CSV | 1,763 ms | 2,885 ms | **no** |
| Dashboard hardware, PDF | 2,789 ms | 1,694 ms | yes |
| Dashboard host-containers, CSV | 12 ms | 7 ms | yes |
| Dashboard host-containers, PDF | 7 ms | 10 ms | yes |
| Dashboard llm-overview, CSV | 1,477 ms | 1,320 ms | yes |
| Dashboard llm-overview, PDF | 1,291 ms | 1,325 ms | yes |
| Dashboard llm-users, CSV | 1,660 ms | 1,678 ms | yes |
| Dashboard llm-users, PDF | 1,655 ms | 1,559 ms | yes |
| Dashboard resources, CSV | 9 ms | 8 ms | yes |
| Dashboard resources, PDF | 6 ms | 7 ms | yes |
| Dashboard stack-health, CSV | 7 ms | 5 ms | yes |
| Dashboard stack-health, PDF | 6 ms | 8 ms | yes |
| Dashboard stack-indexing, CSV | 7 ms | 10 ms | yes |
| Dashboard stack-indexing, PDF | 9 ms | 14 ms | yes |
| Dashboard stack-logs, CSV | 9 ms | 9 ms | yes |
| Dashboard stack-logs, PDF | 14 ms | 9 ms | yes |
| Dashboard stack-performance, CSV | 1,840 ms | 1,877 ms | yes |
| Dashboard stack-performance, PDF | 1,896 ms | 1,656 ms | yes |
| Dashboard system, CSV | 11 ms | 7 ms | yes |
| Dashboard system, PDF | 13 ms | 18 ms | yes |
| Dashboard usage-by-user, CSV | 1,113 ms | 1,094 ms | yes |
| Dashboard usage-by-user, PDF | 1,086 ms | 1,037 ms | yes |
| Prompt log, a model | 14 ms | 8 ms | yes |
| Prompt log, a person | 10 ms | 5 ms | yes |
| Prompt log, a tool called | 10 ms | 5 ms | yes |
| Prompt log, failed | 7 ms | 4 ms | yes |
| Prompt log, newest | 46 ms | 12 ms | yes |
| Prompt log, words | 19 ms | 10 ms | yes |
| Prompt log, words and a model, 30 days | 9 ms | 5 ms | yes |
| Usage by person, CSV | 343 ms | 344 ms | yes |
| Usage by person, one model, Excel | 107 ms | 81 ms | yes |

## What made the exports faster

1. A dashboard's export asked its panels one after another; it asks up to eight at once (each its own connection), as its
   page does.
2. The ledger by hour, `gateway.requests_hourly`, kept by statement triggers on `gateway.requests`: the 38 panels that sum
   or count it read `$__requests`, whole hours from the rollup and the minutes either side from the rows, so every number
   is the rows' own (`LedgerRollupTests` runs each of them both ways over windows that start and end inside an hour, and
   fails when the edges are left out); the usage exports read it too. Percentiles and single requests stay on the rows.

| Export (CSV, warm) | One after another | Eight at once | With the rollup |
|---|---|---|---|
| hardware | 3,936 ms | 3,091 ms | 2,885 ms |
| llm-overview | 3,980 ms | 1,569 ms | 1,320 ms |
| llm-users | 6,763 ms | 2,387 ms | 1,678 ms |
| stack-performance | 3,252 ms | 1,814 ms | 1,877 ms |
| usage-by-user | 6,826 ms | 3,160 ms | 1,094 ms |
| Usage by person | 1,421 ms | 1,268 ms | 344 ms |

(The first two columns were measured with a seed whose every request had its own mix of model, source and project, which
no rollup shrinks; the third with people working as people do. Both seeds are a million rows over 30 days.)

**Left:** Hardware's export lands between 1.7 and 2.9 s: its "Each hour of the day" has the 95th percentile of the time to
the first token by hour over the 30 days, which reads every row (a percentile cannot be summed by hour).

The prompt log answers in milliseconds on a million lines (its newest page, words by trigram index, a model, a tool, a
person, a state), and Accounting (whole, by tokens, narrowed to a model or a person, its PDF, Excel and CSV) in 1.3 to
1.7 s.
