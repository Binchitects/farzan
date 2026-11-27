// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
import { useQuery } from '@tanstack/react-query'
import { Brain, ChevronRight, Wrench } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { api } from '@/lib/api'
import { formatValue, when } from '@/lib/format'
import { Markdown } from '@/pages/chat/markdown'

interface ToolCall {
  id: string | null
  name: string | null
  arguments: string | null
  /** What the tool gave back: null while no later step of its thread carried it. */
  result?: string | null
}

interface StepMessage {
  role: string | null
  content: string | null
  name?: string | null
  toolCallId?: string | null
  reasoning?: string | null
  toolCalls?: ToolCall[] | null
}

interface ApiDetail {
  source: 'api'
  id: string
  at: string
  model: string
  state: string
  error: string | null
  durationMs: number | null
  promptTokens: number | null
  completionTokens: number | null
  chat: string | null
  kept: boolean
  from: number | null
  total: number | null
  messages: StepMessage[]
  tools: string[]
  settings: Record<string, unknown> | null
  answer: { finish: string | null; reasoning: string | null; content: string | null; toolCalls: ToolCall[] | null } | null
}

interface ChatStep {
  id: string
  role: string
  at: string
  model: string | null
  content: string | null
  reasoning: string | null
  toolCalls: ToolCall[] | null
  tool: string | null
  toolCallId: string | null
  details: unknown
  promptTokens: number | null
  completionTokens: number | null
  error: string | null
}

interface ChatDetail {
  source: 'chat'
  id: string
  at: string
  chat: string
  question: string | null
  steps: ChatStep[]
}

interface Run {
  chat: string
  title: string | null
  kind: 'chat' | 'session' | null
  threads: { id: string; name: string }[]
  steps: {
    id: string
    at: string
    thread: string
    model: string
    state: string
    error: string | null
    durationMs: number | null
    tokens: number
    said: string | null
    calls: ToolCall[] | null
  }[]
  questions: { id: string; at: string; text: string }[]
}

/** A part folded until opened: long thinking, a tool's result, a sub-agent's work. Its text is drawn only when open. */
function Fold({ label, icon, children, open = false }: { label: ReactNode; icon?: ReactNode; children: () => ReactNode; open?: boolean }) {
  const [shown, setShown] = useState(open)
  return (
    <div className="rounded-lg border">
      <button type="button" className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm font-medium" aria-expanded={shown} onClick={() => setShown(!shown)}>
        <ChevronRight className={`size-4 transition-transform ${shown ? 'rotate-90' : ''}`} aria-hidden="true" />
        {icon}
        {label}
      </button>
      {shown && <div className="border-t px-3 py-2">{children()}</div>}
    </div>
  )
}

/** Arguments as JSON laid out (as the model wrote them when they are not JSON). */
function laidOut(text: string | null | undefined) {
  const s = text ?? ''
  try {
    return JSON.stringify(JSON.parse(s), null, 2)
  } catch {
    return s
  }
}

/** A tool call: its tool, its arguments, and what the tool gave back (folded), when a later step carried it. */
function Call({ call, children }: { call: ToolCall; children?: ReactNode }) {
  const name = call.name ?? 'a tool'
  return (
    <div className="grid gap-1 rounded-md border px-3 py-2">
      <p className="flex items-center gap-1.5 text-sm font-medium">
        <Wrench className="size-3.5" aria-hidden="true" /> {name}
        {call.id && <span className="font-mono text-xs font-normal text-muted-foreground">{call.id}</span>}
      </p>
      <pre className="max-h-80 overflow-auto rounded-md bg-muted px-3 py-2 font-mono text-xs whitespace-pre-wrap" aria-label={`Arguments of ${name}`}>
        {laidOut(call.arguments) || '(no arguments)'}
      </pre>
      {call.result != null ? (
        <Fold label={`What ${name} gave back (${call.result.length.toLocaleString()} characters)`}>
          {() => <pre className="max-h-96 overflow-auto font-mono text-xs whitespace-pre-wrap" aria-label={`Result of ${name}`}>{call.result || '(nothing)'}</pre>}
        </Fold>
      ) : (
        <p className="text-xs text-muted-foreground">Its result is not kept: no later step of this thread carried it.</p>
      )}
      {children}
    </div>
  )
}

/** The ids of the calls whose results are shown with them, so a result is not shown twice. */
function paired(calls: (ToolCall[] | null | undefined)[]) {
  return new Set(calls.flatMap((c) => c ?? []).filter((c) => c.id && c.result != null).map((c) => c.id as string))
}

function Thinking({ text }: { text: string | null | undefined }) {
  return text ? (
    <Fold label="Thinking" icon={<Brain className="size-4" aria-hidden="true" />}>
      {() => <p className="font-mono text-xs whitespace-pre-wrap">{text}</p>}
    </Fold>
  ) : null
}

/** A message the step sent: a question or the system's words in full, a tool's result folded. */
function Sent({ m, shown }: { m: StepMessage; shown: Set<string> }) {
  if (m.role === 'tool' && m.toolCallId && shown.has(m.toolCallId)) return null
  if (m.role === 'tool')
    return (
      <Fold label={`Result of ${m.name ?? 'a tool'}${m.toolCallId ? ` (${m.toolCallId})` : ''}`}>
        {() => <pre className="max-h-96 overflow-auto font-mono text-xs whitespace-pre-wrap">{m.content}</pre>}
      </Fold>
    )
  return (
    <div className="grid gap-2 rounded-lg border px-3 py-2">
      <Badge variant="outline" className="w-fit">
        {m.role}
      </Badge>
      <Thinking text={m.reasoning} />
      {m.content && <p className="text-sm whitespace-pre-wrap" dir="auto">{m.content}</p>}
      {m.toolCalls?.map((c, i) => <Call key={i} call={c} />)}
    </div>
  )
}

function ApiStep({ d }: { d: ApiDetail }) {
  return (
    <div className="grid gap-4">
      <p className="text-xs text-muted-foreground">
        {[d.model, when(d.at), d.durationMs !== null ? `took ${formatValue(d.durationMs, 'ms')}` : null, d.promptTokens !== null ? `${d.promptTokens.toLocaleString()} in` : null,
          d.completionTokens !== null ? `${d.completionTokens.toLocaleString()} out` : null].filter(Boolean).join(' · ')}
      </p>
      {!d.kept && <p className="text-sm text-muted-foreground">Kept before requests were kept whole: only its question and answer, above.</p>}
      {d.kept && (
        <>
          <section className="grid gap-2" aria-label="What it sent">
            <h3 className="text-sm font-semibold">
              What it sent{d.from ? ` (messages ${d.from + 1} to ${d.total}; those before were its thread's step before)` : ''}
            </h3>
            {d.tools.length > 0 && (
              <p className="flex flex-wrap items-center gap-1 text-xs text-muted-foreground">
                Tools offered: {d.tools.map((t) => <Badge key={t} variant="secondary">{t}</Badge>)}
              </p>
            )}
            {d.settings && Object.keys(d.settings).length > 0 && <p className="font-mono text-xs text-muted-foreground">{JSON.stringify(d.settings)}</p>}
            {(() => {
              const shown = paired(d.messages.map((m) => m.toolCalls))
              return d.messages.map((m, i) => <Sent key={i} m={m} shown={shown} />)
            })()}
          </section>
          <section className="grid gap-2" aria-label="What it got">
            <h3 className="text-sm font-semibold">What it got{d.answer?.finish ? ` (it ended: ${d.answer.finish})` : ''}</h3>
            {!d.answer && <p className="text-sm text-muted-foreground">No answer was kept{d.error ? `: ${d.error}` : '.'}</p>}
            <Thinking text={d.answer?.reasoning} />
            {d.answer?.content && <Markdown text={d.answer.content} />}
            {d.answer?.toolCalls?.map((c, i) => <Call key={i} call={c} />)}
          </section>
        </>
      )}
    </div>
  )
}

const shownInChat = (d: ChatDetail) => paired(d.steps.map((s) => s.toolCalls))

function ChatAnswer({ d }: { d: ChatDetail }) {
  return (
    <div className="grid gap-3">
      {d.question && (
        <div className="rounded-lg border bg-muted/30 px-3 py-2 text-sm whitespace-pre-wrap" dir="auto">
          {d.question}
        </div>
      )}
      {d.steps.map((s) =>
        s.role === 'tool' && s.toolCallId && s.details == null && shownInChat(d).has(s.toolCallId) ? null : s.role === 'tool' ? (
          <Fold key={s.id} label={`Result of ${s.tool ?? 'a tool'}`}>
            {() => (
              <div className="grid gap-2">
                <pre className="max-h-96 overflow-auto font-mono text-xs whitespace-pre-wrap">{s.content}</pre>
                {s.details != null && (
                  <Fold label="Its sub-agents' work">{() => <pre className="max-h-96 overflow-auto font-mono text-xs whitespace-pre-wrap">{JSON.stringify(s.details, null, 2)}</pre>}</Fold>
                )}
              </div>
            )}
          </Fold>
        ) : (
          <div key={s.id} className="grid gap-2 rounded-lg border px-3 py-2">
            <p className="text-xs text-muted-foreground">
              {[s.model, when(s.at), s.completionTokens !== null ? `${s.completionTokens.toLocaleString()} out` : null].filter(Boolean).join(' · ')}
            </p>
            <Thinking text={s.reasoning} />
            {s.content && <Markdown text={s.content} />}
            {s.toolCalls?.map((c, i) => <Call key={i} call={c} />)}
            {s.error && <p className="text-sm text-destructive-ink">{s.error}</p>}
          </div>
        ),
      )}
    </div>
  )
}

function RunView({ chat, onOpen }: { chat: string; onOpen: (id: string) => void }) {
  const run = useQuery({ queryKey: ['admin', 'prompts', 'run', chat], queryFn: () => api<Run>(`/api/admin/prompts/runs/${chat}`) })
  if (!run.data) return <Skeleton className="h-40" />
  const r = run.data
  if (r.steps.length === 0) return <p className="text-sm text-muted-foreground">No step of it came through an API key.</p>
  return (
    <div className="grid gap-4">
      {r.threads.map((t) => (
        <section key={t.id} aria-label={t.name} className="grid gap-1">
          <h3 className="text-sm font-semibold">{t.name}</h3>
          <ol className="grid gap-1">
            {r.steps.filter((s) => s.thread === t.id).map((s, i) => (
              <li key={s.id} className="grid gap-1">
                <button type="button" className="flex w-full items-center gap-2 rounded-md px-2 py-1 text-left text-sm hover:bg-accent" onClick={() => onOpen(s.id)}>
                  <span className="w-6 text-right text-xs text-muted-foreground tabular-nums">{i + 1}</span>
                  <Badge variant="outline">{s.state}</Badge>
                  <span className="min-w-0 flex-1 truncate">{s.said ?? s.error ?? '…'}</span>
                  <span className="text-xs text-muted-foreground tabular-nums">{s.tokens.toLocaleString()} tokens</span>
                </button>
                {s.calls && s.calls.length > 0 && (
                  <div className="ml-8 grid gap-1">
                    {s.calls.map((c, j) => <Call key={j} call={c} />)}
                  </div>
                )}
              </li>
            ))}
          </ol>
        </section>
      ))}
    </div>
  )
}

/** A line of the prompt log in full: this step (or a chat question's answer round by round), and its whole run by thread. */
export function PromptDetail({ source, id, run, onClose }: { source: 'chat' | 'api'; id: string; run: string | null; onClose: () => void }) {
  const [step, setStep] = useState(id)
  const detail = useQuery({
    queryKey: ['admin', 'prompts', 'detail', source, step],
    queryFn: () => api<ApiDetail | ChatDetail>(`/api/admin/prompts/${source}/${step}/detail`),
  })
  const [tab, setTab] = useState('step')
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-4xl">
        <DialogHeader>
          <DialogTitle>{source === 'chat' ? 'The answer, round by round' : 'The request, whole'}</DialogTitle>
          <DialogDescription>Every look is audited, as the log's searches are.</DialogDescription>
        </DialogHeader>
        <Tabs value={tab} onValueChange={setTab}>
          <TabsList>
            <TabsTrigger value="step">{source === 'chat' ? 'This question' : 'This step'}</TabsTrigger>
            {run && source === 'api' && <TabsTrigger value="run">Its run</TabsTrigger>}
          </TabsList>
          <TabsContent value="step" className="pt-3">
            {!detail.data ? <Skeleton className="h-60" /> : detail.data.source === 'api' ? <ApiStep d={detail.data} /> : <ChatAnswer d={detail.data} />}
          </TabsContent>
          {run && source === 'api' && (
            <TabsContent value="run" className="pt-3">
              <RunView
                chat={run}
                onOpen={(s) => {
                  setStep(s)
                  setTab('step')
                }}
              />
            </TabsContent>
          )}
        </Tabs>
        {step !== id && (
          <Button variant="outline" size="sm" className="justify-self-start" onClick={() => setStep(id)}>
            Back to the step opened
          </Button>
        )}
      </DialogContent>
    </Dialog>
  )
}
