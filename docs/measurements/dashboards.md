# Dashboards: what was wrong, and the proof (v8.2)

Measured on the live stack (v8.0.0) on 2026-11-26, read only: Prometheus, the ledger (gateway.requests, in a read-only
transaction) and the app's dashboard API, signed in as the admin.

## What was wrong

| Panel | What it showed | Why | Now |
|---|---|---|---|
| Stack Performance → Capacity | 57.1 tokens a second | the busiest minute of 7 days of each of five models, added up; four were not loaded (Qwen3-4B 37.4, two vLLM test models), on one RTX 3090 | each loaded model's best from its capacity test (`arena_model_capacity_tokens_per_second`); untested models counted apart (**Loaded, not tested**) |
| Of capacity, Room now | from the same sum | the same | of the tested models only |
| Tokens a second, by model and its GPUs | one line per model, `gpus="all"` | the engines are scraped once, not per GPU | **Tokens a second, by GPU**: a model's tokens shared out by the memory it holds on each GPU (`arena_model_gpu_share`) |
| Decode / prefill speed a request, draft acceptance | No data for 30 days | no answer in the 5 minutes: the ratio is 0/0 | says **Idle: no answer in 5 minutes** |
| Ledger decode speed | 54,000 tokens a second for Qwen3-4B | 5,722 of the 10,931 requests imported from LiteLLM have their end as their first token (not streamed) | no first token for those (the importer and the upgrade) |
| Indexing → Longest pass and its row, over 30 days | Loki answered 400 | a range query looked back the whole window at every step: 1,270 h, past Loki's 30 days | asked once, at the window's end |

Sound, checked: the live tokens a second (`rate((n_busy_slots_per_decode * n_decode_total)[1m:15s])`) agrees with
llama.cpp's own count of tokens made within 0.5–9% a day (2,728 against 2,713 for the 27B on 2026-11-13).

## The proof

- Each rewritten query is checked by promtool against engine series of known rates (`DashboardFileTests`): 700
  tokens a second of capacity for two tested models of three, 78.57% of it used, 150 of room, 475 / 75 / 50 on three
  GPUs.
- `deploy/scripts/audit-dashboards.py --truth WINDOW` asks pairs of panels that count the same thing from sources of
  their own, and fails on a gap past the pair's share. On live:

| Check | 30 days | 7 days |
|---|---|---|
| Tokens the engines made against tokens the gateway booked (at most 10% apart) | 2,100,998 against 2,072,823: 1.3% | 2,435 against 2,351: 3.5% |
| Requests: LLM Overview against LLM and users (equal) | 5,435 and 5,435 | 6 and 6 |
| The stack's tokens a second now, two ways (2%) | not on v8.0 (its panel is v8.2's) | — |

Again on live once it ran v8.2.2 (2026-11-26):

| Check | 30 days | 7 days |
|---|---|---|
| Tokens the engines made against tokens the gateway booked (10%) | 1,587,287 against 1,535,800: 3.2% | 7,373 against 7,134: 3.2% |
| Requests: LLM Overview against LLM and users (equal) | 5,172 and 5,172 | 68 and 68 |
| The stack's tokens a second now, two ways (2%), sampled every 5 s while the functional check chatted | 1.73, 5.07, 8.20, 10.02: 0.0% apart each time | — |

The same audit's panel run (6 hours) found **60 GPU queries failing** on live: "found duplicate series for the match group
{uuid=...}". The GPU exporter's update in v8.1 added labels to `nvidia_smi_gpu_info` (index, pci_bus_id...), so across
its restart one GPU had two info series, and every join on it failed for a window that held both. Each query now reads
the info one series a GPU, `max by (instance, uuid, name) (nvidia_smi_gpu_info)`; promtool checks it with two info
series for one GPU (`DashboardFileTests`), and the 45 changed queries answered on live's Prometheus over the same 6
hours.

