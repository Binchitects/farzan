import { Bot, Check, Eye, Search, Send, X } from 'lucide-react'
import { useRef, useState, type FormEvent } from 'react'
import { useLocation } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/input'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { api, ApiError } from '@/lib/api'
import { streamChat } from '@/pages/chat/api'
import { Markdown } from '@/pages/chat/markdown'

type Step = { tool: string; path?: string; words?: string | null; status?: number }

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
  text: string
  steps: Step[]
  proposals: Proposal[]
  error?: string
}

type AssistantEvent =
  | { type: 'text'; text: string }
  | ({ type: 'step' } & Step)
  | ({ type: 'proposal' } & Omit<Proposal, 'state'>)
  | { type: 'error'; message: string }
  | { type: 'done' }

/** What a step did, in words: a search of the API's routes, or a read with how it answered. */
function StepLine({ s }: { s: Step }) {
  return (
    <li className="flex items-center gap-1.5 text-xs text-muted-foreground">
      {s.tool === 'find_api' ? <Search className="size-3" aria-hidden="true" /> : <Eye className="size-3" aria-hidden="true" />}
      {s.tool === 'find_api' ? `Looked for routes${s.words ? `: ${s.words}` : ''}` : `Read ${s.path}`}
      {s.status !== undefined && (
        <Badge variant={s.status < 400 ? 'secondary' : 'warning'} className="px-1 py-0 text-[10px]">
          {s.status}
        </Badge>
      )}
    </li>
  )
}

function ProposalCard({ p, onYes, onNo }: { p: Proposal; onYes: () => void; onNo: () => void }) {
  let body = p.body ?? ''
  try {
    body = JSON.stringify(JSON.parse(body), null, 2)
  } catch {
    // as written
  }
  return (
    <section aria-label="A proposed change" className="grid gap-2 rounded-lg border border-primary/40 bg-primary/5 p-3">
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

/**
 * The admin assistant, on every admin page: ask how to do something, or to do it. It reads Arena as you (your permissions,
 * no more), and a change shows here as what it will do; it is done only when you say yes, and it is in the audit log.
 */
export function AdminAssistant() {
  const location = useLocation()
  const [open, setOpen] = useState(false)
  const [turns, setTurns] = useState<Turn[]>([])
  const [draft, setDraft] = useState('')
  const [busy, setBusy] = useState(false)
  const stop = useRef<AbortController | null>(null)
  // The conversation as it is now (events and answers change it between draws).
  const latest = useRef<Turn[]>([])
  const commit = (next: Turn[]) => {
    latest.current = next
    setTurns(next)
  }
  const update = (i: number, change: (t: Turn) => Turn) => commit(latest.current.map((t, n) => (n === i ? change(t) : t)))

  const ask = async (question: string) => {
    const asked: Turn[] = [...latest.current, { role: 'user', text: question, steps: [], proposals: [] }, { role: 'assistant', text: '', steps: [], proposals: [] }]
    const at = asked.length - 1
    commit(asked)
    setBusy(true)
    const abort = new AbortController()
    stop.current = abort
    try {
      await streamChat(
        '/api/admin-assistant/ask',
        { messages: asked.slice(0, -1).map((t) => ({ role: t.role, content: t.text })), page: location.pathname },
        (raw) => {
          const e = raw as unknown as AssistantEvent
          if (e.type === 'text') update(at, (t) => ({ ...t, text: t.text ? `${t.text}\n\n${e.text}` : e.text }))
          else if (e.type === 'step') update(at, (t) => ({ ...t, steps: [...t.steps, { tool: e.tool, path: e.path, words: e.words, status: e.status }] }))
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
      commit([...latest.current, { role: 'user', text: `No, do not: ${p.what}`, steps: [], proposals: [] }])
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
      <Button className="fixed right-5 bottom-5 z-40 rounded-full shadow-lg" onClick={() => setOpen(true)} aria-label="Ask the admin assistant">
        <Bot /> Assistant
      </Button>
      <Sheet open={open} onOpenChange={setOpen}>
        <SheetContent className="gap-0 sm:max-w-lg">
          <SheetHeader>
            <SheetTitle>Admin assistant</SheetTitle>
            <SheetDescription>It reads as you, with your permissions and no more. A change is done only when you say yes, and is in the audit log.</SheetDescription>
          </SheetHeader>
          <div className="flex-1 overflow-y-auto p-5" aria-live="polite">
            {turns.length === 0 && (
              <p className="text-sm text-muted-foreground">
                Ask about this page or anything you run: why a dashboard is empty, who used the most credit this week, add a person to a project.
              </p>
            )}
            <ol className="grid gap-4" aria-label="The conversation">
              {turns.map((t, i) => (
                <li key={i} className={t.role === 'user' ? 'justify-self-end rounded-lg bg-muted px-3 py-2 text-sm whitespace-pre-wrap' : 'grid gap-2'} dir="auto">
                  {t.role === 'user' ? (
                    t.text
                  ) : (
                    <>
                      {t.steps.length > 0 && <ul className="grid gap-0.5">{t.steps.map((s, n) => <StepLine key={n} s={s} />)}</ul>}
                      {t.text && <Markdown text={t.text} />}
                      {t.proposals.map((p) => (
                        <ProposalCard key={p.id} p={p} onYes={() => void answer(i, p, true)} onNo={() => void answer(i, p, false)} />
                      ))}
                      {t.error && (
                        <p className="text-sm text-destructive-ink" role="alert">
                          {t.error}
                        </p>
                      )}
                      {busy && i === turns.length - 1 && !t.text && <p className="text-sm text-muted-foreground">Looking…</p>}
                    </>
                  )}
                </li>
              ))}
            </ol>
          </div>
          <form onSubmit={submit} className="flex items-end gap-2 border-t p-4">
            <Textarea
              aria-label="Your question"
              value={draft}
              rows={2}
              maxLength={8000}
              onChange={(e) => setDraft(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && !e.shiftKey) submit(e)
              }}
              placeholder="Ask, or say what to do"
            />
            {busy ? (
              <Button type="button" variant="outline" onClick={() => stop.current?.abort()}>
                Stop
              </Button>
            ) : (
              <Button type="submit" aria-label="Send" disabled={!draft.trim()}>
                <Send />
              </Button>
            )}
          </form>
        </SheetContent>
      </Sheet>
    </>
  )
}
