# Plan: what is ongoing and next

This file tracks only work that is in progress or planned. What has shipped is
in [CHANGELOG.md](../CHANGELOG.md), one section per release, and in each
release's tag (`git tag -n99 vX.Y.Z`). An item leaves this file when its
release ships.

## Standing rules

- **Releases.** Every release has a theme and two or three headline features
  that can be shown in a minute. Groundwork goes in between, never as a
  release's whole content. The CHANGELOG's Epics section leads with the
  headlines.
- **How a release is made.** As [development.md](development.md#releasing)
  says: every suite green, `./arenactl check compliance` and
  `tools/release-scan.py` pass, the upgrade from the oldest release works,
  the offline bundle is built and checked, then the tag and
  `gh release create` with the changelog, the `.run`, the SBOM and Code
  Arena's packages.
- **Parity tests stay alive.** `functional-test.py`, `acceptance.py` and
  `audit-auth.sh` test the live stack from the outside, and every release
  passes them. A stack must also deploy from zero on empty volumes.
- **Tests per layer.** xUnit, xUnit with Testcontainers, Vitest and
  Playwright with axe. A port is checked by a comparison test: the same input
  to old and new, the same output.
- **Flaky tests.** Find the race between the test and the app and fix its
  cause. No retries, no sleeps. Ten runs in a row and the whole class pass.
- **Compliance.** A new feature brings its controls, tests and live checks.
  A control made weaker is named under Weakened controls in the changelog.
- **One item at a time.** Each item is committed, pushed and deployed live
  before the next. Downloads (images, models, packages, data sets) need a yes,
  with their size said.
- **Settings.** Everything is set in the app, applied at once where its
  service can take it, else saved with the one command to apply it. No Docker
  socket.

## Open from the 2026-11-06 list

Everything on it shipped in v7.8.0 to v7.8.4, except what needs the hardware
below, and VS Code extensions that need their own code (an extension host, in
Next below). The LMDeploy image is in the bundle since v7.8.4, built from
pypi.org through an HTTP proxy (`arenactl --proxy`).

## Needs the production host (3x H200 NVL)

1. Even load on every GPU, on each engine: done when a load test shows each
   GPU within 10% of the others.
2. 15 people each with the full 262K window on Qwen3.8 27B at once, with
   the advisor's numbers checked against the real memory.
3. The 27B's decode speed: at least 200 tok/s for a person on the fixed
   dashboard.
4. FP4 against FP8 on Hopper: `arenactl check fp4-h200` (the RTX 3090
   numbers are in multi-gpu-server.md).
5. Strata and LMDeploy measured beside vLLM and SGLang.

## Needs Windows

Code Arena on a real Windows machine: no console window, the log beside the
program, and Edge or Chrome 109.

## v8.1 (asked 2026-11-13)

1. **Context stretch on every engine**: RoPE scaling (YaRN and the rest) on vLLM, SGLang, LMDeploy and Strata, as
   llama.cpp has it.
2. **Where the time goes**: how long the model took, the tools, and the person, on each message, each session, in
   total, in Accounting and on the dashboards.
3. **Accounting and project metrics live**: both update as requests are booked, not on a refresh.
4. **Goals and tasks from the sessions**: a session sets its project's goals and tasks and keeps them up to date.
5. **Runs until the goal**: in Code Arena and the chat, an answer cut at its longest length is continued by itself,
   until the goal is met, without the person having to ask.
6. **Every kind of model**: a model that does more than chat (pictures from a text encoder's model, and the rest:
   pottokao/Qwen-Image-2.1-Text-Encoder-Heretic-GGUF) is recognised for all it does, after research into the kinds.
7. **The system's token capacity live**: the Stack Performance dashboard shows how many tokens a second the system
   can make now and makes, in numbers that read right, a line per model and per GPU, not one line for all.
8. **A test bench under Models**: a model's capability (what it answers well) and capacity (how much at once) tested.
9. **Chat sync between Code Arena and the Arena that never misses**.
10. **The sign-in's eyes, the logo and the pattern**: better eyes on the sign-in page, the logo more an owl (the eyes in
    the A kept), and the brand's symbol as a pattern everywhere.
11. **Agents and scheduled tasks in Code Arena** (asked 2026-11-13): Arena's agents (Agent Studio) and scheduled tasks
    made, run and followed from Code Arena too, the IDE and the terminal.

12. **Code Arena's UI tidied** (asked 2026-11-13): the chat's header holds only what is used often, the rest in a
    menu or where it belongs; no tool twice in its tools, and those of no use in Code Arena gone; the tools'
    customization simple; every pane resizable.
13. **Checklists in Code Arena** (asked 2026-11-13): made, edited and deleted there too (the terminal and the IDE), and a
    repository's own (.arena/checklists) for everyone who opens it.

Done (v8.1.0): every item above.

## v8.2 (asked 2026-11-26)

Branch `v8.2` from `origin/v8.1`; `origin/v8.1` (then `origin/main` once 8.1.0 is tagged) merged in often. Code Arena's
C# client gets no big features (the Rust one is its own product): every change to `/api/code-arena/*`, the chat sync
and the key routes stays compatible with both. Measured on the live stack (v8.0.0, read-only: Prometheus, the ledger
in Postgres under a read-only transaction, the app's GET exports) on 2026-11-26. The live ledger is small (10,978
requests, the last on 2026-11-13), so speed is also measured on a seeded ledger of a million requests.

Headlines: **dashboards whose numbers are right, per GPU and per model**; **a prompt log that shows the whole run**
(thinking, every tool call with its input and output, the sub-agents) **with reports written on any filtered log**;
**an assistant for running Arena in every admin page**.

### 1. Tests: fewer, faster, covering more

- **Today.** Llm.Tests about 1,780 cases in 1 h 15 min, CodeArena.Tests 779, CodeIntel.Tests 378, tests/deploy 469
  (about 12 min), web Vitest about 1,780 (213 files), Playwright 28 specs. Counted in the code: 1,297 xUnit facts and
  theories in Llm.Tests (494 InlineData rows), 722 in CodeArena.Tests, 261 in CodeIntel.Tests; 176 carry a
  `[Trait("Control", ...)]`, and docs/compliance/controls.yaml names its own list. Line coverage was never measured
  (coverlet is referenced, never collected). Baseline being measured now, one suite at a time: run time, each test's
  time (TRX), line and branch coverage (Cobertura), and coverage by feature (each source folder: Gateway, Chat,
  Projects, Reports, Dashboards, Identity, ...).
- **Fix.** From the baseline: (a) the slowest tests first: what each spends its time on (a container per class, a
  real wait, a model call) and one shared fixture where the classes only read; (b) tests that cover no line no other
  covers (per-test coverage, by running each class with coverage alone and diffing) go, unless they guard a control
  or a reported bug; (c) near-copies (the same flow with other literals) become one theory; (d) tests of private
  shapes (strings in a page, a JSON's spelling) give way to behaviour through the public route. Kept whatever it
  costs: every test with a Control trait or in controls.yaml (`tools/compliance-docs.py` keeps checking), the parity
  scripts, and each flaky test's fix (its cause, ten runs in a row).
- **Proof.** A table in docs/measurements/tests.md, before and after, per suite: cases, wall time, line and branch
  coverage, coverage per feature. Done when the whole set runs in under half its time today with line coverage not
  lower in any feature, and `./arenactl check compliance` passes.

### 2. Dashboards whose numbers are right

- **Today (measured on live Prometheus).**
  - *Capacity (tokens a second)* reads 57.1 at 2026-11-13 11:00: the sum of each model's highest one-minute rate in
    7 days, five models (Qwen3-4B 37.4, Qwen3.8-27B 10.0, Qwen3-VL-8B 9.5, two vLLM test models 0.13 each) of which
    only one was loaded, on one RTX 3090. It is the busiest minute people made, not what the GPU can make, and it adds
    models that never ran together.
  - *Live tokens a second* (`rate((n_busy_slots_per_decode * n_decode_total)[1m:15s])`) is sound, checked
    2026-11-26: the gauge is llama.cpp's lifetime average, so the product is its count of tokens made as they are made;
    over a day it agrees with `tokens_predicted_total` within 0.5–9% (2,728 against 2,713 for the 27B on 2026-11-13). A
    one-minute window with three samples misreads it (9.47 against 13.35 for Qwen3-VL at 07:30): the windows need at
    least four scrapes.
  - *Decode / Prefill speed a request* and *Draft acceptance*: NO DATA over 30 days in the app's own audit (the 5-minute
    ratios are NaN whenever a window had no request; the `>= 0` filter drops them, so the stat is empty).
  - The ledger's decode speed (tokens / (end − first token)) gives 54,000 tok/s for Qwen3-4B and 126,000 for an
    SGLang model: for a non-streamed answer `first_token_at` is written at the end. 99.5% of API requests (5,947 of
    5,953) carry no conversation.
  - Per GPU is impossible: the engines are scraped with `gpus="all"`, one series for every GPU; the per-GPU panels
    join only nvidia-smi.
  - The app's dashboard audit (`audit-dashboards.py 30d`) says 334 queries "ok", 34 NO DATA, 6 errors (Loki: the
    Indexing page asks 1,270 h, past Loki's 30-day limit), 5 with NaN (System's p95 by service: 370 NaN points). It
    checks that a query answers, never that its number is true.
  - Exports take 0.03–1.3 s on live's small data (Hardware 1.3 s, Stack Performance 0.7 s). (A 404 for the GPU
    dashboard's export was the measuring script's mistake: its uid is `gpu-hardware`.)
- **Fix.** Each panel's query rewritten from the engines' own counters (llama.cpp, vLLM, SGLang, LMDeploy, Strata),
  with the unit and the range said in its title; capacity measured, not guessed: the test bench's (v8.1) highest
  decode rate at full batch per model on its GPUs, kept as a gauge (`arena_model_capacity_tokens_per_second`), with
  "making now" beside it for the loaded models only; the engines scraped per placement with `gpu` labels (the app
  knows which GPUs each model holds) so every panel has a GPU and a model variable; the ledger's first token kept
  null for an answer that was not streamed and decode speed read from the engines; the Loki ranges clamped to its
  limit. A second audit, `audit-dashboards.py --truth`, recomputes each number from the raw
  counters and the ledger and fails on a gap over 2%.
- **Proof.** The truth audit run on live (before: the gaps above; after: none over 2%), screenshots of the pages, a
  test per rewritten query against a recorded Prometheus answer, and exports of every dashboard under 2 s on the
  million-request ledger.

### 3. Accounting that adds up

- **Today (live ledger).** Project MDM: its three chats' answers hold 101,438 tokens in their messages; the ledger
  tags 95,560 to the project; one of its chats (7 answers, 21,097 tokens, made 2026-10-26, before it was put in the
  project) counts nothing, and its Code Arena session's 3 answers before 2026-11-07 neither. A request is counted for a
  project only when it carried the project as it was asked: putting a chat in a project, or taking it out, never moves
  its use. (Measured again: moving a chat does move its requests; the 26 October chat's requests were logged before
  requests carried their chat, 810 such on live.) And **Accounting's projects were always "(in no project)"**: on live,
  all 5,435 requests of the last 30 days, while the ledger tags 6 ($0.0108) to MDM. The ledger's reader gives a uuid
  as text, and Accounting took only a Guid object.
- **Fix.** A project's use is its chats' and keys' requests by conversation and key, whenever they were asked (the
  ledger's `project_id` kept only for requests with no conversation); moving a chat moves its numbers; people,
  models, surfaces, time and money all from the same rows; budgets and the live usage stream from the same query.
- **Proof.** Tests that build a ledger by hand (people, chats moved in and out, sub-agents at once, refused, cached,
  keys) and recompute every figure in the test itself; the same check run on live's MDM project.

### 4. Projects

- **Today.** API use is put in a project only by Code Arena's tag (`project:` prefix): a key has no project
  (ApiKey has no project column), so a plain API user's work is never a project's. Goals and tasks: none on live
  (v8.1's goals from sessions are not live yet: measured again after 8.1.0 is deployed). Contribution counts only
  requests and chat messages, and people's time only from chats.
- **Fix.** Sessions: the project page lists its chats, Code Arena sessions and API keys apart, each with its numbers;
  goals: set and closed from sessions (v8.1) and by hand, checked against their tasks; contribution: each person's
  requests, tokens, money, the model's time and their own, goals and tasks done; **forced projects**: an admin puts a
  project on groups or people (every new chat and session of theirs goes in it, Code Arena told so, the person cannot
  leave it); **project keys**: a key made for a project, its use counted there, with its own limits and budget.
- **Proof.** API tests per rule (forced on a group, on a person, a member who leaves the group), a key's use in the
  project's numbers, the Rust and C# Code Arena clients both taking the forced project (compatibility test on the
  routes), an upgrade test for the new columns.

### 5. The prompt log shows the whole run

- **Today (live).** 172 lines; 69 (40%) say only "Called x, y": no arguments, no results. A line keeps the last
  question and the answer's words, cut at 20,000 characters; no system prompt, no thinking, no earlier turns, no tool
  results, no link between a sub-agent and the run that started it. The web chat's questions are read from the chats,
  with their answers' last words only.
- **Fix.** Each line keeps the request whole (messages, tools offered, reasoning, every tool call with its arguments,
  each result as the next request brought it), compressed, under the log's retention; a run's requests linked by
  conversation, parent and sub-agent; the web chat's steps (tool calls, sub-agents) shown the same way. The page shows a
  run as a tree: thinking, output, input, each tool call, each sub-agent, folded and loaded as opened.
- **Proof.** A test per surface (API, web chat, Code Arena, sub-agents) that the stored line holds every part; the page
  opening a 200-step run under 1 s on the seeded log; retention and deletion tests still pass.

### 6. Search and filters everywhere

- **Today.** The prompt log filters by text, person, source, state, tag and time; its text search is `ILIKE '%x%'` with
  no index (29 ms on live's 2,181 messages, a full scan). Accounting, audits, logs, traces and reports each have their own
  few filters, some none.
- **Fix.** One filter bar for every report and log (person, group, project, model, key, surface, state, tag, tool,
  time, text) kept in the address; full-text search with indexes (pg_trgm or tsvector, no download if PostgreSQL's own).
- **Proof.** Vitest for the bar on each page, API tests per filter, every search under 300 ms on the seeded million rows.

### 7. Reports written on a filtered log

- **Today.** The prompt log's Report counts (by model, person, tag, day) and dashboards export; nothing reads the log
  and writes about it.
- **Fix.** On any filtered log (the prompt log included): Summarize or Write a report, with the model the person picks,
  over what that person may read (prompts.review and the rest), map-reduce for long logs, audited (who, which filter,
  which model), as PDF, Excel or Markdown; ready-made asks (the quality of a person's prompts, a project's progress).
- **Proof.** Tests that a person without the right gets nothing past their reach, the audit line, a report on a
  seeded log checked for its counts.

### 8. An assistant for running Arena, in every admin page

- **Fix.** A side panel on every admin page; its tools are the admin API with the signed-in person's own permissions
  (never more), reading freely, and every change shown as what it will do, done only on the person's yes; audited as
  the person, with the assistant named.
- **Proof.** Tests that a person without a right cannot do it through the assistant, that nothing changes without the
  yes, and Playwright on a few pages (find why a dashboard is empty, add a person to a project).

### 9. Presentations worth showing, and the canvas (asked 2026-11-26)

- **Today (the code; live has no deck and one 19-character canvas).** A deck has seven layouts (title, section,
  bullets, two columns, chart, picture, quote), bar and line charts, at most 8 points a slide and 30 slides; one
  built-in design or the company's template; no tables, shapes, timelines, process steps, big numbers, icons, slide
  numbers, footers, transitions or sections. A deck is made only from words in Decks or the chat's deck tool: never
  from a document, a canvas, a file (PDF, Word, Markdown, a .pptx), a report or a dashboard; an existing .pptx can be a
  template but never opened and revised. Nothing checks that the words fit their boxes. The canvas holds a document or
  code, exports Word, and its versions cannot be compared.
- **Fix.** Decks: layouts for agenda, comparison, table, timeline, process, big numbers, cards, picture with text and
  closing, drawn with PowerPoint's own tables and shapes so they stay editable; pie, doughnut, area, stacked and
  scatter charts; several built-in designs (colour, fonts) beside the company's; slide numbers, footers, sections,
  transitions, links; text fitted (a slide that overflows is split or shrunk, measured on the layout's boxes); made
  from words, an outline, a canvas, a file, a report or a dashboard, the model planning the outline first; a .pptx
  opened, read and revised in place (its slides kept, ours changed). Canvas: a slides kind (a deck in the chat, made
  and changed beside it), versions compared, exports as Word, PDF, Markdown and PowerPoint, the model's edit to a
  selection.
- **Proof.** Tests that each layout and chart opens in a .pptx reader with its parts in place (OOXML schema checked,
  text in its boxes), a revised .pptx keeps the slides it was not asked to change, decks made from each source, and
  screenshots of a made deck in each design.

### 10. Fewer, clearer tools (asked 2026-11-26)

- **Today.** Things that do the same job under different names: the chat's Canvas (documents, code) and its Decks tool
  (slides) both write beside the chat; Scheduled tasks and Agent Studio both run work on a schedule (Studio already
  imports a task); Plans (a chat's steps on a board) and a project's goals and tasks track the same work; "studio" names
  three pages (Code Studio, Agent Studio, Media studio); "Tools as code" sits among the tools though it is a way of
  calling them.
- **Fix (chosen by the user 2026-11-26).** Canvas holds documents, code and slides: a slides canvas is a deck, shown in
  Decks too, and the Decks chat tool goes. Scheduled tasks become Automations' schedule trigger, each task moved to a
  workflow by the upgrade, the /tasks page gone (the routes Code Arena calls keep answering). A plan is its project's
  task board; a chat outside a project keeps its plan in the chat. Code Studio is Code, Agent Studio is Automations,
  Media studio is Media; Tools as code moves into the tool-mode setting.
- **Proof.** The upgrade test moves tasks and plans with nothing lost (counts and each field), Code Arena's routes answer
  as before (both clients), Vitest and Playwright on the renamed pages, the manual and help updated.
- **Done.** Canvas holds slides (v8.2's decks commits); the renames and the tool mode (156d1499); tasks into Automations
  (e7d60731, 6d402cc1: a task is the workflow of its id with its runs, chats, webhook and secret; `/api/hooks/<id>`
  and Code Arena's `/scheduled` answer from workflows; a kept schedule saves past the limits; upgrade tests); plans into
  projects (852a5d66: a project chat's plan is its goal and tasks, tasks with a status, owner, dates and waits, a
  board; upgrade test). Left: Playwright on the renamed pages once a v8.2 stack runs.

### Order

1 (the baseline first: it is running), 2, 3, 4, 5, 6, 7, 8, 9, 10; each committed and pushed on v8.2, `origin/v8.1` merged in
between; then every suite, the compliance check, release-scan, the upgrade from 7.x and 8.x, and (when the installer,
compose or the bundle changed) the offline scenarios, before 8.2.0.

## Next (after v8.2.0)

Left of v8.2 (released 2026-11-26), each to do:

- **Item 1.** Coverage per test, to find single tests whose lines others cover (dotnet-coverage's server mode, a
  snapshot reset between tests).
- **Item 8.** Playwright on a v8.2 stack with a real model, and the Automations schedule spec against it.
- Done since 8.2.2: every export under 2 s on a million requests (the ledger's hourly rollup, `$__requests`, and its times' sketch, `$__spread`), the truth audit on live (and the 60 GPU queries it found failing), goals' contribution checked
  by hand on live ([projects.md](measurements/projects.md); no goal on live yet), the prompt log's requests whole in
  the data export, Accounting narrowed by person, group, project, model, source and kind, the usage exports by model,
  source and project.

## v8.3 (asked 2026-11-26)

Branch `v8.3` from `origin/v8.2`; `origin/v8.2` (then `origin/main` once 8.2.0 is tagged) merged in often. The
framework is its own repository, `Binchitects/farzan-arena-plugin-framework` (MIT): the manifest's schema, the host
API's contract, the SDKs (TypeScript for the UI and for plugins' servers on Node 22), the CLI (`arena-plugin`: init,
build, test, package, sign, verify, publish, dev), docs, examples and a conformance suite. Its plan,
`docs/plan.md` there, holds the architecture, the isolation and security models and the API's versioning; this
section is Arena's side of it.

Headlines: **plugins with code, pages and access to Arena, installed like today's**; **a plugin that breaks takes
only itself down** (the chaos plugin crashes, hangs and eats memory while everything else answers, then is turned
off by itself); **grants an admin can read**: what each plugin may reach, for which groups, and everything it did.

### Plugins v2 extended, not replaced

- **Today.** Plugins v2 is declarative: an OpenAPI document or an MCP server (remote, or Python run per call in the
  sandbox), sign-in, writes, settings, skills, assistants, safeguard rules, toolsets, task templates; catalog with
  SHA-256 and a signed index, grants at install and at updates, group settings, pinning, rollback, health, a tool off
  after 25 failures. An install is an `McpServer` row (`Plugin`, `Manifest`, `PluginSettings`, `PluginPermissions`).
  No plugin code runs, and there is no UI, event, hook, job or data API for plugins.
- **Fix.** One manifest (`apiVersion: arena.plugins/v3`, a superset of v2), one installer, one admin page. New tables
  only (not more columns on `mcp_servers`): `plugin_states` (on, off or tripped and why, the signer, a dev install's
  owner), `plugin_publishers`, `plugin_managers`, `plugin_store` (plugin-wide or per person, with quotas),
  `plugin_events` (at-least-once delivery), `plugin_jobs` (claimed as Automations' are), `plugin_package_blobs`.
  Grants stay where v2 kept them (the install's granted permissions: `scope:` and `host:` keys beside v2's);
  enablement per group is Admin → Tools' access for the plugin's tool row, as for any tool. A migration moves every installed v2 plugin
  into them (manifest rewritten with `apiVersion`, grants carried), with an upgrade test from 7.x and 8.x data.

### The runtime: `plugins`, a service with no network

- A new feature `plugins` (deploy/features, its compose profile; on by default) runs `deploy/services/plugins`: a
  supervisor on `node:22-alpine` (already on this machine), `network_mode: none`, a read-only root, no capabilities
  but SETUID, SETGID and KILL, its memory, CPU and process caps, no Docker socket. The app reaches it over a unix
  socket on the `plugins` volume (Kubernetes: TCP with a key, and a NetworkPolicy with no egress).
- One worker process a plugin, its own uid, its own 0700 folder, rlimits (CPU seconds, processes, open files, file
  size, no core), a V8 heap cap and a resident-memory watchdog, a deadline per call, a bounded queue; started again
  with backoff after a crash. Probed 2026-11-26 in `node:22-alpine`: a heap past its cap aborts, a busy loop is
  killed at its CPU limit, `fetch` fails with ENETUNREACH, the worker runs as its own uid.
- The breaker: 5 failures in a row, or half of the last 20 calls in 10 minutes, turns the plugin off, tells its
  admins, audits `plugin.auto_disabled`.

### The host API, and access

- `/api/plugins` (admin) and the runtime's calls into the app reach one dispatcher: the plugin's granted scopes ∩ the
  acting person's permissions and features ∩ the plugin enabled for their groups. Areas in v1: chats, projects,
  models (through the gateway, as the person), knowledge, files, people and groups, settings, storage, `http.fetch`
  (by the app, to granted hosts only), notify, audit. Events and hooks (observe, or modify with its own grant) with
  deadlines; jobs on cron; code-defined tools for models (they ask first when they write); routes under
  `/api/p/{plugin}/...`, each declaring its access as every route does (EndpointAccessTests).
- New permissions: `plugins.grant`, `plugins.enable`, `feature.plugins.develop` (dev installs, one's own only). Plugin
  scopes are their own catalogue, not entries of `Permissions.All`.
- Every install, grant, enable, disable, auto-disable, setting, outward call, write and denial is audited `plugin.*`.

### The UI

- Slots in the web: pages (`/p/{plugin}/{page}`) and their menu items, panels (chat sidebar, project, admin), settings
  screens, chat composer and message actions, Code Studio and Code Arena panels. Each is an iframe served by the app
  under `/plugin-ui/{plugin}/{version}/` with the preview's CSP (an opaque origin, `connect-src 'none'`), reaching
  Arena only through a checked `postMessage` bridge, inside an error boundary with a load deadline, a heartbeat and
  a rate limit.
- Admin → Plugins: what each plugin asks for and was granted (in words), per-group enablement, managers, state and
  the reason it is off, health, its audit, the trusted publisher keys and whether unsigned packages may install.

### Proof

Tests that break a plugin on purpose, one per item: isolation (another plugin's folder, the network, the socket),
timeouts, resource limits (heap, CPU, fork bomb, file size, open files), crash containment (exit, throw, garbage on
its output), automatic disable (and turning it back on), UI error boundaries (throws, never loads, floods the bridge,
tries the top window, cookies and the API). The framework's conformance suite run against Arena. The upgrade from 7.x
and 8.x with installed v2 plugins. The offline scenarios, since compose, the features and the bundle change.

### Order

Done (2026-11-26, on `v8.3`): 1 to 6, and of 7 the migration with its upgrade test, compose, the feature, Helm, the
docs and the compliance control (OPS-03); the framework released as v0.1.0. To do before 8.3.0: the whole Llm.Tests
run, the offline scenarios (compose and the bundle changed), release-scan and image-scan, the upgrade from 7.x and
8.x on a real install, the framework's 0.1.1; IDE panels, the after_model and before_store hooks and person.created
come later.

1. The framework's contract and CLI (its repository). 2. Its server SDK and harness. 3. The runtime service and its
chaos tests. 4. The registry, the tables and the v2 migration, the host API, tools, routes, events, hooks, jobs,
audit. 5. The UI slots, the bridge, the error boundaries, the admin pages. 6. The UI SDK, `arena-plugin dev` and
`publish`. 7. Compose, the feature, Helm, the offline bundle, docs; every suite, the compliance check, release-scan,
image-scan, the upgrade from 7.x and 8.x, the offline scenarios; then framework 1.0.0 and, after 8.2.0, Arena 8.3.0.

### Engines (asked 2026-11-26, in 8.3.0)

Newest versions of the engines' images, and four engines more: TokenSpeed, FreeToken, Unsloth and Ollama.

- **Where they stand.** vLLM v0.34.0, SGLang v0.5.23, LMDeploy 0.18.0 and Strata 0.2.22 are their latest releases;
  llama.cpp runs the floating `server-cuda` tag (the live stack's copy, from 2026-11-25) and vLLM's PyPI build
  0.31.0. Fix: llama.cpp pinned to a build tag (`server-cuda-b12767`, llamacpp and embed), the PyPI build to v0.34.0;
  the local `server-cuda` tag is never moved (the live stack runs it).
- **The new kinds**, each run by the same supervisor as vLLM (one server a model, on its GPUs), its own feature and
  compose profile (off until asked for), Helm Deployment, launcher (`services/KIND/serve.py`), fake for tests, the
  key by the environment only (IAM-08), readiness on `/health`, its load from `/metrics` where it has one:
  - **TokenSpeed** 0.1.1 (LightSeek, MIT): snapshots on NVIDIA GPUs, tensor parallel, a prefix cache; `ts serve`.
  - **FreeToken** 0.2.3 (FlashML, Apache-2.0): MoE models with experts in RAM on one GPU, GGUF or snapshots;
    `ft serve`; built from PyPI on CUDA 13 (its kernels compiled in the image, nothing at first use).
  - **Ollama** 0.40.18 (MIT): GGUF files of the library made into its models with no pull; it has no key of its own,
    so its launcher fronts it with one, else it is not a managed engine.
  - **Unsloth** (Apache-2.0, beta): weighed and left out (2026-11-26). Its server is Unsloth Studio, an application with
    its own accounts and passwords and the keys it mints itself (`sk-unsloth-`), which cannot take the engine key; its
    image (10.5 GB) carries JupyterLab (a shell on the GPU host) and a Cloudflare tunnel and checks from the internet
    whether its port answers; and what it serves runs on its prebuilt llama.cpp, which Arena runs already, newer.
    Unsloth's own GGUF files (the library's `unsloth/`) run on llama.cpp, Ollama and FreeToken.
- **Where it stands** (2026-11-26): FreeToken and Ollama done and run for real on the RTX 3090 (SmolLM2-135M from the
  library: the key asked, answers through the launcher; FreeToken's first start compiles its kernels, about 3 minutes);
  llama.cpp pinned to b12767 (1.1.0-dev, every preset key the app writes still a flag; run for real); the vLLM PyPI
  build pinned to 0.34.0 (the same vLLM, torch and GGUF plugin as arena-vllm; not rebuilt here). TokenSpeed's launcher
  waits for its image (7.3 GB, the proxy cut its first pull), to check its key, metrics and readiness on it.
- **Proof.** Each launcher's arguments, key and GPUs (test_engine_launchers.py), its kind's settings in the app
  (EngineKindTests, VllmBackendTests, the advisor), the form (engine-fields), parity with Helm, and each engine run
  for real on this machine's GPU with a model of the library. The images scanned, the bundle and offline scenarios
  made again.

## Next

1. **Farzan Graph's code graphs.** Who calls a method, what implements an interface,
   what a merge request breaks across every repository a person can read, with
   the lines, tests and owners. A Code map page, call and type hierarchy in
   Code Arena, an Impact card on reviews; `symbol_graph`, `mr_impact` and
   `service_contracts` for agents. Design: [code-intel-graph.md](design/code-intel-graph.md),
   31 commits; the headline is reachable at commit 27. Done: 1 (access kept as
   GitLab project ids, repository row ids never reused), 2 (the allowlist in
   the SQL of the graph tools, impact_of and semantic search), 3 (a name several
   repositories provide settled only by the user's own manifests; candidates
   shared among the readable), 4 (secrets taken out of configuration files
   before they are stored, and out of those stored before), and the symbol graph inside
   each repository with `symbol_graph` (callers, callees, references, implementations,
   bases; Code Arena's scanners linked). Still to do: stable symbol keys, cross-repository
   symbol edges, tests and owners, `mr_impact`, contracts, the Code map page, and the
   measured gates.
2. **The app's own gateway**, commits 10 to 21 of
   [own-gateway.md](design/own-gateway.md). Measured on the offline host
   (2026-11-11, [gateway.md](measurements/gateway.md)): every gate passes,
   6 ms overhead at p95 against LiteLLM's 17 ms; two app replicas, a pool of two
   copies, a copy killed, speech and transcription all pass. LiteLLM is gone
   (commits 19 to 21: the code, the deployment, the docs), with its keys and
   log imported once on an upgrade and its database dropped after the backup.
   Still to do: the day's soak (`gateway-soak.py`, started 2026-11-11) before
   it is merged into v8.0; clients-check.py on the H200 host; the importer goes
   in the release after 8.0.
3. **Evals.** Test sets for assistants, prompts and models, run on every
   change, scored by rules or a judge model, what got worse shown first.
4. **Code Arena's extensions.** An extension host for VS Code extensions that
   need their own code (ESLint, GitLens, Pylance).
5. **Flaky tests to fix at their cause:** `ConcurrencyTests.A_model_asked_for_at_the_engines_limit_unloads_an_idle_one_not_kept_never_the_big_one`,
   `StdioChildTests.A_child_that_listens_is_reached_on_its_port_and_one_that_ends_first_says_why`,
   `DebugSessionTests.Each_response_goes_to_the_page_that_asked_with_its_own_number_and_events_go_to_every_page`
   (the stop took 8.7 s on CI, past the 5 s its waits add up to: something else waits)
   (each failed once under a full suite's load, and passes alone).

## Groundwork, in between

1. vLLM and SGLang measured on the H200 host: a smoke test per model, numbers,
   and the people load tests; DCGM's image.
2. `code-intel graph-eval` with the owners' links of the company's repositories,
   and the priors calibrated from it.
3. Reviews: plain git repositories, two refs compared on Gitea, and GitHub's
   and Gitea's webhooks for automatic reviews.
4. The chat's default model with more answers at once, or a second copy, where
   people wait for it.
5. Kubernetes: CloudNativePG in the chart, and backups to object storage.
6. Measurements: Fix it on CVEfixes (needs a yes), qiskit-ibm-runtime for the
   quantum set's 51 device problems (needs a yes), chemistry's and physics'
   lab sets.
7. The science lab: a team of models on one problem, plots in cells, and
   telling a person when the model's context is too small for its tools.
8. Tool modes: the graph questions (need a measured person with GitLab
   rights), and tools on demand a direct-mode model passes over.
9. Carried: Code Arena's sandbox on Windows; Laya's routing quality; deep
   research under six minutes.

## Later, each needing a yes

FedRAMP's baselines; NIST AI 600-1; NIS2 and DORA; ISO/IEC 27701, 27017 and
27018; a FIPS mode built on OpenSSL's validated provider.
