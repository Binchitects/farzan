# Projects: contribution checked by hand on live (v8.2.2)

On 2026-11-26 the live stack (v8.2.2) held two projects: **MDM** (3 chats, 15 requests, one refused) and **proj2**
(1 chat, 4 requests). Neither had a goal or a task yet, so goals and tasks done could not be checked on live data;
they are checked by the API tests (`ProjectTests`), which build them by hand.

The check: each person's numbers on MDM's project report (`GET /api/projects/{id}/report`, as the admin) against
the same numbers worked out apart, in Python, from the ledger's own rows (`gateway.requests` where `project_id` is
MDM's, refusals left out). The model's time is each person's requests' spans merged where they overlap, counted
here with a sweep of its own, not the report's SQL.

| Person | Requests | Tokens (in / cached / out) | Spend | The model's time | Messages |
|---|---|---|---|---|---|
| test | 5 and 5 | 91,950 (34,740 / 54,770 / 2,440), the same | $0.009995, the same | 251.7 s, the same | 4 and 4 |
| Administrator | 9 and 9 | 28,686 (8,847 / 18,531 / 1,308), the same | $0.003186, the same | 169.1 s, the same | 3 and 3 |

Every figure matched (report first, by hand second). The people's own time (240 s and 180 s) is a minute a
question here, as its rule says for a question after a break past 15 minutes or a chat's first.
