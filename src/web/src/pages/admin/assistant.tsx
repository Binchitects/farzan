import { ArrowUp, Bot, Check, Square, X } from 'lucide-react'
import { useRef, useState, type FormEvent } from 'react'
import { createPortal } from 'react-dom'
import { useLocation } from 'react-router'
import { Button } from '@/components/ui/button'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { api, ApiError } from '@/lib/api'
import { cn } from '@/lib/utils'
import { streamChat } from '@/pages/chat/api'
import { waitingText } from '@/pages/chat/format'
import { Markdown } from '@/pages/chat/markdown'
import { Thinking, ToolCard, useFollow } from '@/pages/chat/parts'
import type { Message, ToolCall } from '@/pages/chat/types'

/** One step it took (a search of the API's routes, a read), as the chat's tool calls are shown: running, then what came back. */
interface Step {
  id: string
  tool: string
  arguments: string
  state: 'running' | 'done' | 'failed'
  status?: number
  ms?: number
  result?: string
}

interface Proposal {
  id: string
  method: string
  path: string
  body: string | null
  what: string
  /** waiting; done or failed with the status it got; declined. */
  state: 'waiting' | 'sending' | 'done' | 'failed' | 'declined'
  status?: number
}

interface Turn {
  role: 'user' | 'assistant'
  /** A question's words; an answer's, round by round (the last one still coming while it answers). */
  text: string
  rounds: string[]
  reasoning: string
  thinkingMs: number | null
  thinkingSince: number | null
  steps: Step[]
  proposals: Proposal[]
  error?: string
}

type AssistantEvent =
  | { type: 'thinking'; text: string }
  | { type: 'thought'; ms: number }
  | { type: 'delta'; text: string }
  | { type: 'replace'; text: string }
  | { type: 'round' }
  | ({ type: 'step' } & Step)
  | ({ type: 'proposal' } & Omit<Proposal, 'state'>)
  | { type: 'error'; message: string }
  | { type: 'done' }

const blank = (role: Turn['role'], text = ''): Turn => ({ role, text, rounds: [], reasoning: '', thinkingMs: null, thinkingSince: null, steps: [], proposals: [] })

/** An answer's words: its rounds, each a paragraph. */
const said = (t: Turn) => t.rounds.filter((r) => r.trim()).join('\n\n')

/** A step as the chat's tool card takes it: its call, and its result once it came. */
function asCall(s: Step): ToolCall {
  return { id: s.id, function: { name: s.tool, arguments: s.arguments } }
}

function asResult(s: Step): Message | undefined {
  if (s.state === 'running') return undefined
  return {
    id: `${s.id}-result`, parentId: null, role: 'tool', content: s.result ?? '', reasoning: null, toolName: s.tool, toolCallId: s.id, toolCalls: null, attachments: [],
    status: s.state === 'failed' ? 'failed' : 'complete', error: s.state === 'failed' && s.status ? `HTTP ${s.status}` : null, model: null, promptTokens: null, cachedTokens: null,
    completionTokens: null, thinkingMs: null, durationMs: s.ms ?? null, createdAt: new Date().toISOString(), noAccess: false,
  }
}

function ProposalCard({ p, onYes, onNo }: { p: Proposal; onYes: () => void; onNo: () => void }) {
  let body = p.body ?? ''
  try {
    body = JSON.stringify(JSON.parse(body), null, 2)
  } catch {
    // as written
  }
  return (
    <section aria-label="A proposed change" className="my-2 grid animate-enter gap-2 rounded-lg border border-primary/40 bg-primary/5 p-3">
      <p className="text-sm font-medium">{p.what}</p>
      <p className="font-mono text-xs text-muted-foreground">
        {p.method} {p.path}
      </p>
      {body && <pre className="max-h-40 overflow-auto rounded bg-muted px-2 py-1 font-mono text-xs whitespace-pre-wrap">{body}</pre>}
      {p.state === 'waiting' || p.state === 'sending' ? (
        <div className="flex gap-2">
          <Button size="sm" onClick={onYes} loading={p.state === 'sending'}>
            <Check /> Yes, do it
          </Button>
          <Button size="sm" variant="outline" onClick={onNo} disabled={p.state === 'sending'}>
            <X /> No
          </Button>
        </div>
      ) : (
        <output className={p.state === 'failed' ? 'block text-sm text-destructive-ink' : 'block text-sm text-muted-foreground'}>
          {p.state === 'done' ? `Done (HTTP ${p.status}).` : p.state === 'failed' ? `It failed (HTTP ${p.status}).` : 'Not done: you said no.'}
        </output>
      )}
    </section>
  )
}

/** A question, as the chat shows one: its bubble on the right. */
function Question({ text }: { text: string }) {
  return (
    <section className="flex animate-enter flex-col items-end gap-1" aria-label="You">
      <div dir="auto" className="bidi-lines max-w-[85%] rounded-2xl rounded-br-md bg-secondary px-4 py-2.5 text-[0.9375rem] leading-relaxed whitespace-pre-wrap text-secondary-foreground">
        {text}
      </div>
    </section>
  )
}

/** An answer, as the chat shows one: its thinking, its steps as tool cards, its words as they come, and while nothing has come, the wait. */
function Answer({ t, live, onAnswer }: { t: Turn; live: boolean; onAnswer: (p: Proposal, yes: boolean) => void }) {
  const words = said(t)
  const running = t.steps.some((s) => s.state === 'running')
  const thinkingNow = live && !!t.reasoning && t.thinkingMs === null && !words
  return (
    <section className={cn('min-w-0 animate-enter', live && 'pb-2')} aria-label="Answer" aria-busy={live}>
      {t.reasoning && <Thinking text={t.reasoning} live={thinkingNow} ms={t.thinkingMs} since={t.thinkingSince} />}
      {t.steps.map((s) => (
        <ToolCard key={s.id} call={asCall(s)} result={asResult(s)} live={live} />
      ))}
      {words && <Markdown text={words} live={live} />}
      {live && words && <span className="ml-0.5 inline-block h-4 w-1.5 animate-pulse rounded-sm bg-primary align-middle" aria-hidden="true" />}
      {t.proposals.map((p) => (
        <ProposalCard key={p.id} p={p} onYes={() => onAnswer(p, true)} onNo={() => onAnswer(p, false)} />
      ))}
      {t.error && (
        <p className="my-2 text-sm text-destructive-ink" role="alert">
          {t.error}
        </p>
      )}
      {live && !words && !thinkingNow && !running && (
        <output className="flex items-center gap-2 text-sm text-muted-foreground">
          <span className="flex gap-1" aria-hidden="true">
            <span className="size-1.5 animate-bounce rounded-full bg-current [animation-delay:-0.3s]" />
            <span className="size-1.5 animate-bounce rounded-full bg-current [animation-delay:-0.15s]" />
            <span className="size-1.5 animate-bounce rounded-full bg-current" />
          </span>
          {waitingText(null)}
        </output>
      )}
    </section>
  )
}

/**
 * The admin assistant, on every admin page: ask how to do something, or to do it. It reads Arena as you (your permissions,
 * no more), and a change shows here as what it will do; it is done only when you say yes, and it is in the audit log. It
 * looks and moves as the chat does: its thinking, its steps as tool cards, its words as they come.
 */
export function AdminAssistant() {
  const location = useLocation()
  const [open, setOpen] = useState(false)
  const [turns, setTurns] = useState<Turn[]>([])
  const [draft, setDraft] = useState('')
  const [busy, setBusy] = useState(false)
  const stop = useRef<AbortController | null>(null)
  const box = useRef<HTMLDivElement>(null)
  const onScroll = useFollow(box, turns, busy)
  // The conversation as it is now (events and answers change it between draws).
  const latest = useRef<Turn[]>([])
  const commit = (next: Turn[]) => {
    latest.current = next
    setTurns(next)
  }
  const update = (i: number, change: (t: Turn) => Turn) => commit(latest.current.map((t, n) => (n === i ? change(t) : t)))

  const ask = async (question: string) => {
    const asked: Turn[] = [...latest.current, blank('user', question), { ...blank('assistant'), rounds: [''] }]
    const at = asked.length - 1
    commit(asked)
    setBusy(true)
    const abort = new AbortController()
    stop.current = abort
    try {
      await streamChat(
        '/api/admin-assistant/ask',
        { messages: asked.slice(0, -1).map((t) => ({ role: t.role, content: t.role === 'user' ? t.text : said(t) })), page: location.pathname },
        (raw) => {
          const e = raw as unknown as AssistantEvent
          if (e.type === 'thinking') update(at, (t) => ({ ...t, reasoning: t.reasoning + e.text, thinkingSince: t.thinkingSince ?? Date.now(), thinkingMs: null }))
          else if (e.type === 'thought') update(at, (t) => ({ ...t, thinkingMs: (t.thinkingMs ?? 0) + e.ms }))
          else if (e.type === 'delta') update(at, (t) => ({ ...t, rounds: [...t.rounds.slice(0, -1), (t.rounds.at(-1) ?? '') + e.text] }))
          else if (e.type === 'replace') update(at, (t) => ({ ...t, rounds: [...t.rounds.slice(0, -1), e.text] }))
          else if (e.type === 'round') update(at, (t) => ({ ...t, rounds: [...t.rounds, ''] }))
          else if (e.type === 'step')
            update(at, (t) => {
              const step: Step = { id: e.id, tool: e.tool, arguments: e.arguments, state: e.state, status: e.status, ms: e.ms, result: e.result }
              return { ...t, steps: t.steps.some((s) => s.id === e.id) ? t.steps.map((s) => (s.id === e.id ? step : s)) : [...t.steps, step] }
            })
          else if (e.type === 'proposal') update(at, (t) => ({ ...t, proposals: [...t.proposals, { id: e.id, method: e.method, path: e.path, body: e.body, what: e.what, state: 'waiting' }] }))
          else if (e.type === 'error') update(at, (t) => ({ ...t, error: e.message }))
        },
        abort.signal,
      )
    } catch (e) {
      if (!abort.signal.aborted) update(at, (t) => ({ ...t, error: e instanceof ApiError ? e.message : 'The assistant could not answer.' }))
    } finally {
      setBusy(false)
    }
  }

  const answer = async (turn: number, p: Proposal, yes: boolean) => {
    const set = (change: Partial<Proposal>) => update(turn, (t) => ({ ...t, proposals: t.proposals.map((q) => (q.id === p.id ? { ...q, ...change } : q)) }))
    if (!yes) {
      await api(`/api/admin-assistant/changes/${p.id}/no`, { method: 'POST' }).catch(() => undefined)
      set({ state: 'declined' })
      // The no goes with the next question, so it knows.
      commit([...latest.current, blank('user', `No, do not: ${p.what}`)])
      return
    }
    set({ state: 'sending' })
    try {
      // Only the status goes back to the model: an answer may hold a new password or key, which is yours alone.
      const r = await api<{ status: number }>(`/api/admin-assistant/changes/${p.id}/yes`, { method: 'POST' })
      const ok = r.status < 400
      set({ state: ok ? 'done' : 'failed', status: r.status })
      await ask(ok ? `Yes. It was done: HTTP ${r.status}. Check it, then say what is next, if anything.` : `Yes, but it failed: HTTP ${r.status}. Why, and what now?`)
    } catch (e) {
      set({ state: 'failed', status: e instanceof ApiError ? e.http : undefined })
    }
  }

  const submit = (e: FormEvent) => {
    e.preventDefault()
    const q = draft.trim()
    if (!q || busy) return
    setDraft('')
    void ask(q)
  }

  return (
    <>
      {/* On the page itself, not in its content: it floats in its corner however far the page is scrolled. */}
      {createPortal(
        <Button className="fixed right-5 bottom-5 z-40 rounded-full shadow-lg" onClick={() => setOpen(true)} aria-label="Ask the admin assistant">
          <Bot /> Assistant
        </Button>,
        document.body,
      )}
      <Sheet open={open} onOpenChange={setOpen}>
        <SheetContent className="gap-0 sm:max-w-lg">
          <SheetHeader>
            <SheetTitle>Admin assistant</SheetTitle>
            <SheetDescription>It reads as you, with your permissions and no more. A change is done only when you say yes, and is in the audit log.</SheetDescription>
          </SheetHeader>
          <div ref={box} onScroll={onScroll} className="flex-1 overflow-y-auto px-5 py-4" aria-live="polite">
            {turns.length === 0 && (
              <p className="text-sm text-muted-foreground">
                Ask about this page or anything you run: why a dashboard is empty, who used the most credit this week, add a person to a project.
              </p>
            )}
            <div className="grid gap-5" aria-label="The conversation" role="log">
              {turns.map((t, i) =>
                t.role === 'user' ? (
                  <Question key={i} text={t.text} />
                ) : (
                  <Answer key={i} t={t} live={busy && i === turns.length - 1} onAnswer={(p, yes) => void answer(i, p, yes)} />
                ),
              )}
            </div>
          </div>
          <form onSubmit={submit} className="p-4 pt-2">
            <div className="rounded-2xl border bg-card shadow-sm transition-shadow focus-within:border-primary/50 focus-within:shadow-md">
              <textarea
                aria-label="Your question"
                value={draft}
                rows={2}
                maxLength={8000}
                dir="auto"
                onChange={(e) => setDraft(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && !e.shiftKey) submit(e)
                }}
                placeholder="Ask, or say what to do"
                className="block max-h-48 w-full resize-none bg-transparent px-4 pt-3 text-[0.9375rem] leading-relaxed outline-none placeholder:text-muted-foreground"
              />
              <div className="flex justify-end px-2 pt-1 pb-2">
                {busy ? (
                  <Button type="button" size="icon-sm" variant="secondary" className="animate-pop rounded-full" onClick={() => stop.current?.abort()} aria-label="Stop">
                    <Square className="fill-current" />
                  </Button>
                ) : (
                  <Button type="submit" size="icon-sm" className="animate-pop rounded-full" disabled={!draft.trim()} aria-label="Send">
                    <ArrowUp />
                  </Button>
                )}
              </div>
            </div>
          </form>
        </SheetContent>
      </Sheet>
    </>
  )
}
