# A million rows: exports, reports and the prompt log (v8.2.3)

Measured on 2026-11-26 with `MillionRowMeasurements` (`tools/dn test tests/Llm.Tests -c Release --filter MillionRow -e ARENA_MEASURE_MILLION=1`):
a million requests in the ledger and a million prompt log lines over the last 30 days, seeded in 30 s in the test's own
Postgres, vacuumed and analysed, on the live stack's host (so the numbers move with what live is doing). Each asked twice,
cold then warm; the target is under 2 s warm. No Prometheus or Loki: their panels answer at once, the ledger's are measured.

| What | Cold | Warm | Under 2 s |
|---|---|---|---|
| Accounting, 30 days | 1,307 ms | 1,288 ms | yes |
| Accounting, CSV | 1,155 ms | 1,142 ms | yes |
| Accounting, Excel | 1,156 ms | 1,158 ms | yes |
| Accounting, PDF | 1,152 ms | 1,156 ms | yes |
| Accounting, by tokens | 1,152 ms | 1,150 ms | yes |
| Accounting, one model | 909 ms | 909 ms | yes |
| Accounting, one person | 1,006 ms | 969 ms | yes |
| Dashboard code-intel, CSV | 28 ms | 8 ms | yes |
| Dashboard code-intel, PDF | 8 ms | 10 ms | yes |
| Dashboard gpu-hardware, CSV | 10 ms | 7 ms | yes |
| Dashboard gpu-hardware, PDF | 13 ms | 9 ms | yes |
| Dashboard hardware, CSV | 2,988 ms | 3,091 ms | **no** |
| Dashboard hardware, PDF | 2,360 ms | 1,933 ms | yes |
| Dashboard host-containers, CSV | 10 ms | 6 ms | yes |
| Dashboard host-containers, PDF | 6 ms | 8 ms | yes |
| Dashboard llm-overview, CSV | 1,653 ms | 1,569 ms | yes |
| Dashboard llm-overview, PDF | 1,357 ms | 1,357 ms | yes |
| Dashboard llm-users, CSV | 1,974 ms | 2,387 ms | **no** |
| Dashboard llm-users, PDF | 1,991 ms | 2,048 ms | **no** |
| Dashboard resources, CSV | 5 ms | 7 ms | yes |
| Dashboard resources, PDF | 6 ms | 6 ms | yes |
| Dashboard stack-health, CSV | 7 ms | 6 ms | yes |
| Dashboard stack-health, PDF | 6 ms | 7 ms | yes |
| Dashboard stack-indexing, CSV | 6 ms | 9 ms | yes |
| Dashboard stack-indexing, PDF | 5 ms | 6 ms | yes |
| Dashboard stack-logs, CSV | 7 ms | 6 ms | yes |
| Dashboard stack-logs, PDF | 8 ms | 6 ms | yes |
| Dashboard stack-performance, CSV | 1,780 ms | 1,814 ms | yes |
| Dashboard stack-performance, PDF | 1,739 ms | 1,730 ms | yes |
| Dashboard system, CSV | 7 ms | 6 ms | yes |
| Dashboard system, PDF | 5 ms | 5 ms | yes |
| Dashboard usage-by-user, CSV | 2,132 ms | 3,160 ms | **no** |
| Dashboard usage-by-user, PDF | 2,302 ms | 2,293 ms | **no** |
| Prompt log, a model | 6 ms | 3 ms | yes |
| Prompt log, a person | 8 ms | 4 ms | yes |
| Prompt log, a tool called | 7 ms | 4 ms | yes |
| Prompt log, failed | 6 ms | 4 ms | yes |
| Prompt log, newest | 38 ms | 5 ms | yes |
| Prompt log, words | 8 ms | 4 ms | yes |
| Prompt log, words and a model, 30 days | 7 ms | 4 ms | yes |
| Usage by person, CSV | 1,318 ms | 1,268 ms | yes |
| Usage by person, one model, Excel | 459 ms | 481 ms | yes |

Exports of a dashboard asked its panels one after another; they now ask up to eight at once (each its own connection),
as the page does:

| Dashboard export (CSV) | One after another | Eight at once |
|---|---|---|
| hardware | 3,936 ms | 3,091 ms |
| llm-overview | 3,980 ms | 1,569 ms |
| llm-users | 6,763 ms | 2,387 ms |
| stack-performance | 3,252 ms | 1,814 ms |
| usage-by-user | 6,826 ms | 3,160 ms |

**Left:** Hardware's, LLM and users' and Usage by person's exports land between 1.9 and 3.2 s from one run to the next
(the host is shared with live). Each of their ledger panels reads the 30 days' million rows (0.4 to 1.1 s each: Cost
by person 1.1 s, the busiest hours of the week 1.1 s), and more at once only shares the database's time between them
(16 at once measured no faster). They need an hourly rollup of the ledger for their panels (sums and distinct people
by hour, person, model and source; percentiles stay on the rows): in plan.md.

The prompt log answers in milliseconds on a million lines (its newest page, words by trigram index, a model, a tool, a
person, a state), and Accounting (whole, by tokens, narrowed to a model or a person, its PDF, Excel and CSV) and the
usage exports in 0.4 to 1.3 s.
