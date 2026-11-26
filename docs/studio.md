# Automations

Workflows that do the work as you. A trigger starts one. Its steps ask a
model, use tools, decide, loop and wait for your yes. What they give out
goes to the bell, a room, an email, a chat channel, GitLab or a file. Every
run is kept, with each step as it went, to read and to replay. What were
scheduled tasks are workflows here ([On a schedule](#on-a-schedule)).

## Making a workflow

**Workspace → Automations.** Press **New workflow**, or start from a
template: Arena's own, or one a plugin offers (marked **From** the plugin: a
question on its schedule, with the plugin's tool). A new workflow starts **Off**: it runs only by hand until you switch
it **On**. For code review the templates are **Review each merge request**
(GitLab's merge request event, Code's review of it as a comment),
**Weekly repository health** (a whole repository reviewed each Monday, its
summary by email when it needs changes) and **Weekly security of a
product** (several repositories' security looked at across, under your
bell).

The canvas shows the workflow from the top down: the trigger, then the
steps in order. Choose the trigger or a step to change it in the panel
beside the canvas. Press **+** between two steps to add one there.
**Save** keeps your changes, and **Run now** runs the saved workflow.

### What starts it

- **By hand**: **Run now**, with words to start from, or with fields of your
  own.
- **On a schedule**: chosen in words (every day, weekdays, once a week or a
  month, every few hours or minutes, at a time) or written as cron (five
  fields, `0 8 * * 1-5` is weekdays at 8), in a time zone. More below.
- **On a webhook**: any system posts JSON to its address, with its secret in
  the `X-Hook-Secret` header. The secret is shown once; **A new secret**
  replaces it.
- **On GitLab events**: give GitLab the workflow's address as a webhook, and
  its secret as the secret token, with the events it should hear. The
  event's words (a merge request's changes, the end of a failed job's log)
  are the input, and its facts are values of their own (below). The events:

  | Event | When |
  |---|---|
  | `merge_request` | a merge request opens, opens again, or gets new commits (with its changes) |
  | `merge_request_approved`, `merge_request_merged`, `merge_request_closed` | a merge request is approved, merged or closed |
  | `pipeline_failed`, `pipeline_succeeded` | a pipeline fails (with the end of its failed jobs' logs) or passes |
  | `job_failed` | one job fails (with the end of its log) |
  | `issue`, `issue_closed` | an issue opens or opens again; an issue is closed |
  | `comment` | someone comments on a merge request, an issue or a commit (not a system note) |
  | `push`, `tag` | commits are pushed to a branch (not a branch deleted); a tag is pushed |
  | `release` | a release is made or changed |
  | `deployment` | a deployment succeeds or fails |
  | `wiki_page` | a wiki page is made or changed |

  **Branches** keeps it to some branches (globs, comma separated:
  `main, release/*`): a merge request's target, or the branch a push, a
  pipeline, a job or a deployment ran on. **Labels** keeps it to merge
  requests and issues with one of them. **A comment holds** keeps comments to
  those with one of its words (a mention such as `@arena`). A comment the
  app made itself (it ends with its signature, "by Farzan Arena for …") never
  starts a run, so a workflow that comments cannot start itself again.
- **On a room's message**: a message in a room that starts with its word
  (`#triage`). The rest of the message is the input, and `{{input.by}}` is
  who wrote it. What the workflow itself says in the room starts nothing.
- **From a form**: fields you choose (a line, paragraphs, or a choice;
  needed or not). Share the workflow, and others fill its form.

### Steps

| Step | What it does |
|---|---|
| **Ask a model** | One question to your model. Its answer can be JSON, read as `{{id.json.…}}`. **The model for small steps** is quicker and cheaper. |
| **Agent with tools** | The model with the tools you choose (the web, code search, your data…; none chosen: those on in a new chat), as a chat of yours to open from the run, with its thinking as you choose. **Carry on one chat**: each run asks in the chat of the run before, so it reads what was said ("what changed since yesterday"). |
| **Use a tool** | One function of a tool you may use, with its arguments. A function that asks a person first (a browser's send, an API's write) is not run by a workflow. |
| **Run Python** | Python in the sandbox, with no network, for at most 2 minutes. The run's values are in `input.json` beside it (`json.load(open('input.json'))`); what it prints is its answer, and its JSON when it prints JSON. Needs the Python tool. |
| **Search company knowledge** | The company's documents you may read, nearest the words first: titles, links and passages, and `{{id.json}}` a list of them. |
| **Ask a data source** | A data source you may ask (Ask your data), with a question in words (the model writes the SQL) or with SQL run as it is. Its rows are a table, and `{{id.json.rows}}` each row by its columns' names (`{{id.json.rows.0.Total}}`). |
| **Call a web address** | GET, POST, PUT, PATCH or DELETE to a public address (never the stack's or the office network's, whatever a name points to; the chat's allowed sites apply), with headers and a body made of values; 30 seconds. What comes back is its answer, and its JSON when it is. Each call is in the audit log (`workflow.http`). Headers are kept in the workflow as written: whoever may change it reads them. |
| **If** | A test (contains, is, is more than, is empty…), then its **Yes** steps or its **No** steps. |
| **For each** | Its steps for each item of a list: a step's JSON list, or one item a line; at most 50. `{{item}}` is the item, `{{index}}` its number from 0. |
| **Ask me first** | The run waits for your answer, and a notification tells you. **Yes, go on** carries on from there; **No, stop it** ends the run. |
| **Wait** | The run waits a number of minutes (at most a week), kept in the database, and goes on where it was, on any replica, even after a restart. |
| **End the run** | Ends it there, done or failed (failed tells you, with why). |
| **Write text** | Words made of what came before; **It is JSON** reads them as JSON too. |
| **Say in a room** | A message in a room you write in, from you, for its people. |
| **Email me** | An email to your account's address. |
| **Notify me** | A notification in your bell (and on your phone, where you turned that on). |
| **Post to a chat channel** | A post to a Slack, Teams, Mattermost or Discord incoming webhook: `text` (and `content`) with the words, and `workflow`, `run`, `at` beside them. Its address is a secret: kept encrypted (`APP_DATA_KEY`), never shown again (the step says it is set; type a new one to replace it), and only to hosts an admin allows (Settings → Automations → **Webhook hosts**), so a workflow cannot reach into the network. |
| **Comment in GitLab** | A comment on the merge request or issue that started the run (a GitLab trigger). |
| **Act in GitLab** | With the app's GitLab bot: comment on a merge request (`!12`) or an issue (`#34`), add labels to one, or open an issue in a project. The project and the merge request or issue are the event's when left empty. Each act is in the audit log (`workflow.gitlab_comment`, `workflow.gitlab_label`, `workflow.gitlab_issue`). |
| **Save a file** | A file of yours, to download from the run. |
| **Review code** | A review as you, waited for (at most six hours): a merge request (its project and `!number`, or the one whose GitLab event started the run), a whole repository, several (looked at across), one repository's security or several's, at a branch, tag or commit or each one's default. The review's summary (or its look across the repositories, with what is open) is the step's answer, its page the link. Needs `review.run`; each is in the audit log (`workflow.review`). |

Any text in a step may hold values. Press **{}** to put one in where the
cursor is:

- `{{input.text}}` is the run's input, and `{{input.by}}` who started it;
- `{{input.<field>}}` is a field's value;
- `{{<step id>.text}}` is what a step before it gave, and
  `{{<step id>.json.…}}` its JSON (a part of it by its path: `rows.0.Total`);
- `{{input.gitlab.event}}`, `.project`, `.branch`, `.title`, `.url`, `.iid`,
  `.author` and `.labels` are a GitLab event's facts, and `{{input.event.…}}`
  its whole payload;
- `{{now}}` is the date and time.

## On a schedule

What were **scheduled tasks** are workflows whose trigger is a schedule (or
a webhook, or GitLab's events): a morning digest, a weekly report on a
repository, a review of every merge request. The template **Morning digest**
is one to start from.

- **The schedule** may not run a workflow more often than **Most often**
  (Settings → Automations, 15 minutes by default), and a person has at most
  **Scheduled workflows per person** (10) on a schedule. With **Schedules**
  off there, no schedule runs and none can be set.
- **Each run** is the owner's: their model, tools and credit, in turn with
  everyone else's answers. Nobody is in an agent step's chat to press
  **Allow**, so its model is not offered **Deep research** while it asks
  first (Admin → Tools).
- **What it gives out** is its steps': **Notify me** under the bell,
  **Email me** (an admin sets the mail server under **Settings → Email**),
  **Post to a chat channel**, **Comment in GitLab** on the merge request or
  issue whose event started it. Reading a merge request's changes and a
  failed job's log, and commenting, use the **GitLab bot token** an admin
  sets (Settings → Automations: a bot account's token with scope `api`,
  Reporter in the projects), never Farzan Graph's read-only token.
- A schedule that came while the app was down runs once when it is back.
  With several app replicas, one runs it: the replica that moves its next
  time on first.
- Runs of one workflow do not wait for each other: two events that come
  together run side by side.
- **Code Arena** lists the workflows of yours that run by themselves (a
  schedule, a webhook, GitLab) under **Scheduled in Arena**, and runs one
  now (`/schedule arena N`).

**After the upgrade to 8.2** each scheduled task is the workflow of the same
id, kept as it was: on or off, its next run, its schedule or events, its
question as an agent step (its model, thinking and tools, one chat carried
on when it had that) or its review as a review step, then **Notify me**,
and **Email me**, **Post to a chat channel** (its webhook's address still
encrypted) and **Comment in GitLab** as it delivered. Its runs are the
workflow's, each with its chat or review, and its old address
`/api/hooks/<id>` still takes its events with the same secret. A role that
could schedule tasks (`feature.tasks`) may use Automations
(`feature.studio`); whoever was denied scheduling is denied Automations.
Plugins' tasks are Automations' templates. An event waiting for a task's
run when the upgrade came is not run.

## Runs

**Runs** lists every run, the newest first. Open one to see it two ways:

- **Replay** walks its steps one by one on the canvas, each with what it was
  given and what it gave.
- **Timeline** lists all its steps.

A step that failed says why, and the run stops there. **Stop** ends a run
that is going or waiting. When a run goes on after **Ask me first**, the
steps that already ran are not run again: their answers are kept.

A run takes at most 300 steps (each turn of a loop counts).

## Sharing

**Settings → Who may run it**: only you, some groups (yours), or everyone.
Those it is shared with run it by hand or fill its form. It runs as you,
with your tools and your credit. They see how it went, but not what it read
or what each step gave, except its **Write text** steps.

Workflows need the `feature.studio` permission. Everyone holds it unless an
admin denies it.
