# Code Arena

Code Arena, Farzan Arena's own coding agent, is its own project:
**github.com/Binchitects/code-arena**, in Rust, for Linux, macOS and Windows,
working with every Arena from 8.0 on. Its manual, its releases and how to build
it are there. This page says what this Arena serves it. (Until 8.2, a C# Code
Arena, its IDE in the browser and Code's workspaces on the server were part of
this repository; they left it with 8.2.)

## Getting it

From its releases: one file, put on your PATH. **Your account → Connect your
tools → Code Arena** gives the lines to sign in:

```bash
code-arena login --url https://DOMAIN              # asks for your API key
code-arena login --url https://DOMAIN --ca ca.crt  # the Arena's certificate from the company's own CA
code-arena doctor                                  # what this Arena offers it, feature by feature
```

It talks only to this Arena, with the person's API key: the model through the
gateway, and Arena's tools as the person. `feature.code_arena` (Admin → Access)
says who may use it.

## Chats in Arena

Each session is a chat of the person's in Arena (`/api/code-arena/chats`): its
messages both ways, kept in step after a compaction on either side, a turn the
web sees as it is written and may queue a message to (`queue/take`), a draft,
and answers' times and usage booked as any chat's.

## Projects

A session in a project (`/project` in the client) counts toward it, as a chat
does, and keeps the project's goals and tasks with the project tools
(`project-work`): the same as a chat's in the project ([chat.md](chat.md#projects)).

## The plan

Its working plan, steps with what done means, the files each changes and how
its check went, is kept with its chat (`/api/code-arena/chats/{id}/plan`), shown
on the web and changed only by the agent.

## Checklists

The person's checklists (`/api/code-arena/checklists`), ticked by the session
as the web's chat ticks them.

## Import and export skills

The person's skills, and their plugins' and assistants', read and added to
(`/api/code-arena/settings`, the skills routes), as on the Skills page.

## What it reads besides

Its capabilities (`/api/code-arena/capabilities`), the person (`me`), models
and their windows (`config`, `tokenize`), memories (`memories`, `recall`,
`import`), preferences, Automations' agents and the workflows of the person's
that run by themselves (`agents`, `scheduled`), and `watch` for what changes on
the web. Every route checks the person's key on each call.
