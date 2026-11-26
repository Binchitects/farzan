# Test suites: time and coverage (v8.2)

Measured on the development host (one machine, the live stack running beside it), 2026-11-26.
Before: `v8.2` as branched from `v8.1` (commit 8f1db797's parent). After: `v8.2` at the commit that adds
this file. Coverage is of lines and branches, generated code left out (`obj/`, migrations, `.g.cs`,
`.Designer.cs`), merged across shards (a line covered in any shard is covered). How:
`tools/test-shards.py` runs a suite, `tools/test-report.py` reads its TRX and Cobertura files.

## Llm.Tests (the app and its API)

| | Before | After |
|---|---|---|
| Tests | 1,780 | 1,798 (+18: prompt log reports, admin assistant, accounting, forced projects, project keys, decks) |
| Wall time, with coverage | 1 h 57 min (one process, every class in turn) | **37.1 min** (3 shards side by side, built once) |
| Test time summed | 104.3 min | 65.3 min |
| Llm.Api lines / branches | 88.6% / 73.7% | 88.7% / 73.4% |
| Llm.Core lines / branches | 97.7% / 79.8% | 97.7% / 79.4% |

The time is the shards': `tools/test-shards.py tests/Llm.Tests --shards 3 --coverage` gives each shard its
own classes (balanced by their measured times, kept in `tests/Llm.Tests/test-times.json`), its own copy of the
build (coverage rewrites the assemblies, and three processes rewriting the same files crashed a test host at its
start) and its own Postgres and app. The three took 35.6, 36.1 and 37.1 minutes. The summed time fell too: the
before run shared the machine with a heavier load.

The three failures of the after run were `ComplianceControlsTests` reading `docs/compliance/controls.yaml`,
edited during the run for tests the run's build did not have yet; they pass on the build that has them.

The slowest classes are the engines' (EngineMoveTests: a model moved between llama.cpp, vLLM and SGLang and back,
36 s the slowest test), ConcurrencyTests (40 tests, 208 s), LdapTests (a real directory) and Code Arena's sync.
They run real processes and real waits for a model's load: they stay, as they prove what nothing else does.

## The other suites

Before: as branched. After: `v8.2` at 9d0ddb44, run the same way (`tools/dn test tests/<suite> -c Release --collect "XPlat
Code Coverage"`), with Llm.Tests' leave-out coverage run going beside them (the times read high for it).

| Suite | Tests | Wall time, before → after | Test time summed | Lines | Branches |
|---|---|---|---|---|---|
| CodeArena.Tests | 794 (779 run, 15 skipped) | 1 min 53 s → 2 min 0 s | 12.3 → 12.8 min | 84.1% → 84.1% (CodeArena) | 74.6% → 74.7% |
| CodeIntel.Tests | 378 | 45 s → 50 s | 0.9 → 0.9 min | n/a → **74.5%** (CodeIntel) | n/a → **65.9%** |
| tests/deploy (unittest) | 475 | 13 min 56 s | | n/a | |
| Web (vitest) | 1,771 → 1,776 | about 4 min | 31.8 min summed (beside the server runs) | 77.4% → 77.5% | 70.2% |

Neither .NET suite changed in v8.2: their numbers are the same, the times within the machine's load. CodeIntel.Tests had
no coverage collector, so its coverage was never measured: it has coverlet.collector now (6.0.4, the one Llm.Tests
uses), and 74.5% of its lines. Its slowest class is PlatformHttpTests (8 s); nothing there to cut for time.

The web suite had flaky tests under load (a lazy page's code loaded after the test looked, a fake model's
advice missing a field, an element replaced while clicked): fixed at their cause (each test file preloads the
pages it draws, full fakes, waiting for the element the test acts on), and each passed 10 runs in a row.

## Not done yet

Cutting tests that prove nothing another does not: none was cut yet. The candidates are tests that only repeat a
broader one's path (by coverage per test) and real waits that a test clock would make instant.
