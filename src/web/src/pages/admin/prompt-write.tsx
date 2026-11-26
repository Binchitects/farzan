import { useQuery } from '@tanstack/react-query'
import { Download, FileText } from 'lucide-react'
import { useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Textarea } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { ApiError } from '@/lib/api'
import { configQuery, streamChat } from '@/pages/chat/api'
import { Markdown } from '@/pages/chat/markdown'

/** The log's filters as the report takes them (the page's search, its time window as from and to). */
export interface LogFilters {
  q: string
  person: string
  source: string
  state: string
  tag: string
  model: string
  tool: string
  run: string
  from: string | null
  to: string | null
}

type WriteEvent =
  | { type: 'progress'; done: number; of: number; lines: number }
  | { type: 'report'; markdown: string; lines: number; parts: number; window: string; filters: string }
  | { type: 'error'; message: string }

/** Ready-made asks: what a reviewer most often wants to know of a filtered log. */
const asks = [
  { label: 'Prompt quality', text: 'How good are these prompts? Where are they unclear, missing context or asking for the wrong thing, with examples, and how could the person ask better?' },
  { label: 'Project progress', text: 'What was worked on, what got done, what is stuck or keeps failing, and what is next, in order of time?' },
  { label: 'Failures', text: 'What went wrong: the failed, refused and stopped answers, what they have in common, and what would fix them?' },
  { label: 'Topics', text: 'What are these questions about? The main topics with how many questions each, and examples.' },
]

const DEFAULT = '__default__'

/**
 * A report on the lines the log's filters find, written by a model the reviewer chooses (as them): their question (or a
 * ready-made one), the model, then the report as it is written, to read and to keep as Markdown. It is audited.
 */
export function WriteReport({ filters, onClose }: { filters: LogFilters; onClose: () => void }) {
  const config = useQuery(configQuery)
  const [ask, setAsk] = useState(asks[0].text)
  const [model, setModel] = useState(DEFAULT)
  const [progress, setProgress] = useState<{ done: number; of: number; lines: number } | null>(null)
  const [report, setReport] = useState<Extract<WriteEvent, { type: 'report' }> | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [writing, setWriting] = useState(false)
  const stop = useRef<AbortController | null>(null)

  const write = async () => {
    setWriting(true)
    setProblem(null)
    setReport(null)
    setProgress(null)
    const abort = new AbortController()
    stop.current = abort
    const body = {
      ask: ask.trim(),
      model: model === DEFAULT ? null : model,
      q: filters.q || null,
      person: filters.person || null,
      source: filters.source === 'all' ? null : filters.source,
      state: filters.state === 'all' ? null : filters.state,
      tag: filters.tag || null,
      searchModel: filters.model || null,
      tool: filters.tool || null,
      run: filters.run || null,
      from: filters.from,
      to: filters.to,
    }
    try {
      await streamChat(
        '/api/admin/prompts/write',
        body,
        (e) => {
          const w = e as unknown as WriteEvent
          if (w.type === 'progress') setProgress(w)
          else if (w.type === 'report') setReport(w)
          else if (w.type === 'error') setProblem(w.message)
        },
        abort.signal,
      )
    } catch (e) {
      if (!abort.signal.aborted) setProblem(e instanceof ApiError ? e.message : 'The report could not be written.')
    } finally {
      setWriting(false)
    }
  }

  const download = () => {
    if (!report) return
    const url = URL.createObjectURL(new Blob([report.markdown], { type: 'text/markdown' }))
    const a = document.createElement('a')
    a.href = url
    a.download = `prompt-log-report-${new Date().toISOString().slice(0, 10)}.md`
    a.click()
    URL.revokeObjectURL(url)
  }

  return (
    <Dialog
      open
      onOpenChange={(o) => {
        if (!o) {
          stop.current?.abort()
          onClose()
        }
      }}
    >
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>Write a report on these lines</DialogTitle>
          <DialogDescription>
            On what the log's filters find now (up to 2,000 lines, newest first), by the model you choose, as you: your credit and safeguards. It is in the audit log.
          </DialogDescription>
        </DialogHeader>
        <div className="grid gap-3">
          <fieldset className="flex flex-wrap gap-2" aria-label="Ready-made asks">
            {asks.map((a) => (
              <Button key={a.label} type="button" size="sm" variant={ask === a.text ? 'secondary' : 'outline'} onClick={() => setAsk(a.text)}>
                {a.label}
              </Button>
            ))}
          </fieldset>
          <Field label="What the report is about">
            <Textarea value={ask} maxLength={2000} onChange={(e) => setAsk(e.target.value)} rows={3} />
          </Field>
          <Field label="Model" className="w-72">
            <Select value={model} onValueChange={setModel}>
              <SelectTrigger aria-label="Model">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={DEFAULT}>The default{config.data?.model ? ` (${config.data.model})` : ''}</SelectItem>
                {config.data?.models.map((m) => (
                  <SelectItem key={m.name} value={m.name} disabled={!m.loaded && !m.onRequest}>
                    {m.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
          <div className="flex gap-2">
            <Button onClick={() => void write()} loading={writing} disabled={ask.trim().length === 0}>
              <FileText /> Write it
            </Button>
            {writing && (
              <Button variant="ghost" onClick={() => stop.current?.abort()}>
                Stop
              </Button>
            )}
          </div>
          {writing && progress && (
            <output className="block text-sm text-muted-foreground">
              {progress.done < progress.of - 1
                ? `Reading part ${progress.done + 1} of ${progress.of - 1} (${progress.lines.toLocaleString()} lines)…`
                : `Writing the report on ${progress.lines.toLocaleString()} lines…`}
            </output>
          )}
          {problem && (
            <p className="text-sm text-destructive-ink" role="alert">
              {problem}
            </p>
          )}
          {report && (
            <section aria-label="The report" className="grid gap-2 rounded-lg border px-4 py-3">
              <p className="text-xs text-muted-foreground">
                {report.lines.toLocaleString()} lines, {report.window}
                {report.filters ? `; ${report.filters}` : ''}
              </p>
              <Markdown text={report.markdown} />
              <Button variant="outline" size="sm" className="justify-self-start" onClick={download}>
                <Download /> Markdown
              </Button>
            </section>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}
