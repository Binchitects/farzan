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

## At the release (v8.2.0)

The suite on the release's code, with coverage, 3 shards: **1,752 tests in 33.2 minutes** (48.7 test-minutes summed;
1 h 57 min and 104 summed at the start of v8.2). Llm.Api **86.1%** of its lines and 72.3% of its branches, Llm.Core 96.1%
and 76.9%. Lower than the 88.7% above: Code Arena left the repository with its C# client, whose end-to-end tests
(CodeArenaLiveSyncTests and the rest, 46 tests) drove much of the server's sync from a real client; the routes the
Rust client calls keep their HTTP-level tests (CodeArenaSyncTests and the others), and the client's own tests are its
repository's. The run's five failures were fixed and their classes run again (the gateway's test, a race with the
ledger's booking, 10 runs in a row).

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

## What a test covers that no other does

Cutting by name or by feel drops checks; cutting by coverage alone does too. Measured instead: the suite run with
coverage twice on the same code, whole and without a group of candidates (`tools/test-shards.py --exclude`), and the
lines and branches only the group covers counted.

- **Llm.Tests.** The candidates: the 396 tests over 3 s, leaving out the control-tagged ones, the upgrade tests and the
  real-engine, concurrency, LDAP and scale-out classes (38 of the 65 summed minutes). Without them the wall time with
  coverage is 24.4 min (38.5 whole, beside other work): but they alone cover **18,519 lines and 11,089 branches in 323
  files** (Code Arena's sync, downloads, knowledge connectors, media and vector jobs, Automations' runner, the data
  sources, plugins, meetings). They are the features' whole-path tests; no smaller test walks those paths.
- **Web.** The 176 tests over 3 s alone cover 2,401 statements and 2,651 branches. Each measured alone: 23 cover nothing
  the rest do not (2.2 of their 14.9 minutes), but each pins what coverage cannot see (a price as shown, a refusal, where
  focus goes, a search asked once typing pauses, a permission's absence), so none was cut.
- **Made faster instead.** A fresh app's database is a copy of one migrated once a run (56 s → 46 s on four classes);
  the fixed sleeps wait for a sign (JobRunnerTests 16 s → 5 s; the engine watcher's, media loop's and LDAP check's rounds,
  the GPU's line, the browser's hand-back); the manual's anchor test checks each page's Markdown (60 s → 8 s).

## Not done yet

Each of the 396 server candidates measured alone was tried and stopped: a run's coverage collector instruments Llm.Api
and Llm.Core before the first test (about ten minutes, paid once a shard in the runs above), so 396 single runs would
take some 66 hours. Coverage per test needs a collector that keeps its instrumented build between runs, or resets its
counts between tests in one run; until then, the leave-out runs above are what decide.

## Coverage per test (2026-11-26)

With dotnet-coverage's session reset after each test ([coverage-per-test.md](coverage-per-test.md); coverlet had
needed ten minutes a run): 1,752 tests ran 105,632 lines of Llm.Api and Llm.Core; 1,007 of them, chosen greedily, run
every one of those lines, and 745 run only lines they run too. Those are the candidates to look at, one by one: a test
outside the cover may still be the only one that checks an answer, a refusal or a number.
