import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { KeyRound, Power, ScrollText, ShieldCheck, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Alert } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { useConfirm } from '@/components/ui/confirm'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Field } from '@/components/ui/field'
import { Input, Textarea } from '@/components/ui/input'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { PluginFrame } from '@/plugins/plugin-frame'
import { pluginsQuery } from '@/plugins/plugins'

interface State {
  state: 'on' | 'off' | 'tripped'
  reason: string | null
  changedAt: string | null
  signer: string | null
  publisher: string | null
  untrustedSigner: string | null
  dev: string | null
  health: { calls: number; failures: number; inARow: number; lastError: string | null; lastErrorAt: string | null }
}

interface Grant {
  key: string
  text: string
  granted: boolean
  needsYes: boolean
  risk: 'low' | 'medium' | 'high' | null
}

const short = (fp: string) => `${fp.slice(0, 8)}…${fp.slice(-4)}`

/**
 * A plugin's v3 part on its card: whether it is on (and why not: an admin, or its breaker), who signed it, what it was
 * granted, its worker in the runtime with its logs, and its own settings screen. Each action is its permission's.
 */
export function PluginRuntimePanel({ id, name, may }: { id: string; name: string; may: { enable: boolean; grant: boolean } }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const state = useQuery({ queryKey: ['admin', 'plugins', id, 'state'], queryFn: ({ signal }) => api<State>(`/api/admin/plugins/${id}/state`, { signal }) })
  const mine = useQuery(pluginsQuery)
  const [grants, setGrants] = useState(false)
  const [worker, setWorker] = useState(false)
  const [settings, setSettings] = useState(false)
  const toggle = useMutation({
    mutationFn: (on: boolean) => api(`/api/admin/plugins/${id}/state`, { method: 'PUT', body: { on } }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['admin', 'plugins', id, 'state'] })
      void queryClient.invalidateQueries({ queryKey: ['plugins', 'mine'] })
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const s = state.data
  if (!s) return null
  const own = mine.data?.find((p) => p.name === name)
  return (
    <div className="grid gap-2 rounded-lg border p-3 text-xs" data-plugin-state={s.state}>
      <div className="flex flex-wrap items-center gap-2">
        {s.state === 'on' ? <Badge variant="success">On</Badge> : s.state === 'tripped' ? <Badge variant="destructive">Turned itself off</Badge> : <Badge variant="outline">Off</Badge>}
        {s.publisher ? <Badge variant="outline"><ShieldCheck className="size-3" aria-hidden /> Signed by {s.publisher}</Badge>
          : s.untrustedSigner ? <Badge variant="warning">Signed by an untrusted key {short(s.untrustedSigner)}</Badge>
          : <Badge variant="warning">Not signed by a trusted publisher</Badge>}
        {s.dev && <Badge variant="outline">Dev install</Badge>}
        <span className="text-muted-foreground">{s.health.calls} calls here · {s.health.failures} failed{s.health.inARow > 0 ? ` · ${s.health.inARow} in a row` : ''}</span>
      </div>
      {s.reason && <p className={s.state === 'tripped' ? 'text-destructive-ink' : 'text-muted-foreground'}>{s.reason}</p>}
      {s.health.lastError && <p className="text-muted-foreground">Last failure: {s.health.lastError}</p>}
      <div className="flex flex-wrap gap-2">
        {may.enable && (
          <Button size="sm" variant="outline" loading={toggle.isPending} onClick={async () => {
            if (s.state === 'on' && !(await confirm({ title: 'Turn it off?', description: 'Its tools, pages, hooks, events and jobs stop at once, for everyone.', confirm: 'Turn off' }))) return
            toggle.mutate(s.state !== 'on')
          }}>
            <Power /> {s.state === 'on' ? 'Turn off' : 'Turn on'}
          </Button>
        )}
        <Button size="sm" variant="outline" onClick={() => setGrants(true)}><KeyRound /> What it may reach</Button>
        <Button size="sm" variant="outline" onClick={() => setWorker(true)}><ScrollText /> Worker and logs</Button>
        {own?.settings && <Button size="sm" variant="outline" onClick={() => setSettings(true)}>Its own settings</Button>}
      </div>
      <GrantsDialog id={id} open={grants} onClose={() => setGrants(false)} mayGrant={may.grant} />
      <WorkerDialog id={id} open={worker} onClose={() => setWorker(false)} />
      {own?.settings && (
        <Dialog open={settings} onOpenChange={setSettings}>
          <DialogContent className="sm:max-w-2xl">
            <DialogHeader><DialogTitle>{own.title}: its own settings</DialogTitle><DialogDescription>The plugin's screen, in its own frame.</DialogDescription></DialogHeader>
            <PluginFrame plugin={own} slot="settings" id="settings" entry={own.settings} />
          </DialogContent>
        </Dialog>
      )}
    </div>
  )
}

function GrantsDialog({ id, open, onClose, mayGrant }: { id: string; open: boolean; onClose: () => void; mayGrant: boolean }) {
  const queryClient = useQueryClient()
  const grants = useQuery({ queryKey: ['admin', 'plugins', id, 'grants'], queryFn: ({ signal }) => api<Grant[]>(`/api/admin/plugins/${id}/grants`, { signal }), enabled: open })
  const [chosen, setChosen] = useState<Set<string> | null>(null)
  const current = chosen ?? new Set(grants.data?.filter((g) => g.granted).map((g) => g.key))
  const save = useMutation({
    mutationFn: () => api(`/api/admin/plugins/${id}/grants`, { method: 'PUT', body: { granted: [...current] } }),
    onSuccess: () => {
      toast.success('Saved: it has what is ticked, from its next call on.')
      void queryClient.invalidateQueries({ queryKey: ['admin', 'plugins'] })
      setChosen(null)
      onClose()
    },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Dialog open={open} onOpenChange={(o) => { if (!o) { setChosen(null); onClose() } }}>
      <DialogContent className="sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>What it may reach</DialogTitle>
          <DialogDescription>What it asks for, and what is granted. A call is allowed only within what is granted and what the person using it may do themselves.</DialogDescription>
        </DialogHeader>
        <ul className="grid gap-2 text-sm">
          {grants.data?.map((g) => (
            <li key={g.key} className="flex items-start gap-2">
              <input type="checkbox" id={`grant-${g.key}`} className="mt-1" disabled={!mayGrant} checked={current.has(g.key)}
                onChange={(e) => { const next = new Set(current); if (e.target.checked) next.add(g.key); else next.delete(g.key); setChosen(next) }} />
              <label htmlFor={`grant-${g.key}`} className="grid gap-0.5">
                <span className="flex items-center gap-2 font-mono text-xs">{g.key}{g.risk === 'high' && <Badge variant="warning">high</Badge>}</span>
                <span className="text-muted-foreground">{g.text}</span>
              </label>
            </li>
          ))}
        </ul>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>Close</Button>
          {mayGrant && <Button loading={save.isPending} onClick={() => save.mutate()}>Save</Button>}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function WorkerDialog({ id, open, onClose }: { id: string; open: boolean; onClose: () => void }) {
  const runtime = useQuery({
    queryKey: ['admin', 'plugins', id, 'runtime'], enabled: open, refetchInterval: open ? 5000 : false,
    queryFn: ({ signal }) => api<{ available: boolean; why: string | null; worker: Record<string, unknown> | null; logs: string[] }>(`/api/admin/plugins/${id}/runtime`, { signal }),
  })
  const w = runtime.data?.worker as { running?: boolean; pid?: number; uid?: number; rssKb?: number; restarts?: number; calls?: number; waiting?: number; backoffMs?: number } | null | undefined
  return (
    <Dialog open={open} onOpenChange={(o) => { if (!o) onClose() }}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader><DialogTitle>Its worker</DialogTitle><DialogDescription>Its process in the plugin runtime, apart from Arena and every other plugin.</DialogDescription></DialogHeader>
        {runtime.data && !runtime.data.available && <Alert variant="warning">{runtime.data.why}</Alert>}
        {w ? (
          <p className="text-sm">{w.running ? 'Running' : 'Not running'} · user {w.uid} · {Math.round((w.rssKb ?? 0) / 1024)} MB · started again {w.restarts} times · {w.calls} calls now, {w.waiting} waiting{w.backoffMs ? ` · waits ${Math.ceil(w.backoffMs / 1000)} s before it starts again` : ''}</p>
        ) : <p className="text-sm text-muted-foreground">Not started yet: it starts on its first call.</p>}
        <pre className="max-h-80 overflow-auto rounded bg-muted p-2 text-xs" aria-label="Its logs">{runtime.data?.logs.join('\n') || 'No logs yet.'}</pre>
      </DialogContent>
    </Dialog>
  )
}

/** The publishers whose packages are trusted here: their public keys, by name. */
export function PublishersCard({ mayGrant }: { mayGrant: boolean }) {
  const queryClient = useQueryClient()
  const confirm = useConfirm()
  const list = useQuery({ queryKey: ['admin', 'plugins', 'publishers'], queryFn: ({ signal }) => api<{ fingerprint: string; name: string; addedAt: string }[]>('/api/admin/plugins/publishers', { signal }) })
  const [name, setName] = useState('')
  const [pem, setPem] = useState('')
  const add = useMutation({
    mutationFn: () => api('/api/admin/plugins/publishers', { body: { name, publicKeyPem: pem } }),
    onSuccess: () => { setName(''); setPem(''); void queryClient.invalidateQueries({ queryKey: ['admin', 'plugins', 'publishers'] }); toast.success('Trusted.') },
    onError: (e) => toast.error(errorMessage(e)),
  })
  const remove = useMutation({
    mutationFn: (fp: string) => api(`/api/admin/plugins/publishers/${fp}`, { method: 'DELETE' }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['admin', 'plugins', 'publishers'] }),
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Card aria-label="Trusted publishers">
      <CardHeader>
        <CardTitle>Trusted publishers</CardTitle>
        <CardDescription>A plugin that runs code or adds pages installs only when signed by one of these keys (arena-plugin keygen makes one; its .pub.pem goes here).</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-3">
        {list.data?.length === 0 && <p className="text-sm text-muted-foreground">None yet.</p>}
        <ul className="grid gap-1 text-sm">
          {list.data?.map((p) => (
            <li key={p.fingerprint} className="flex items-center gap-2">
              <ShieldCheck className="size-4" aria-hidden /> {p.name} <span className="font-mono text-xs text-muted-foreground">{short(p.fingerprint)}</span>
              <span className="flex-1" />
              {mayGrant && (
                <Button size="icon-sm" variant="ghost" aria-label={`Stop trusting ${p.name}`} onClick={async () => {
                  if (await confirm({ title: `Stop trusting ${p.name}?`, description: 'Plugins it signed stay installed; their updates and new installs need another trusted signature.', confirm: 'Stop trusting', destructive: true })) remove.mutate(p.fingerprint)
                }}><Trash2 /></Button>
              )}
            </li>
          ))}
        </ul>
        {mayGrant && (
          <div className="grid gap-2">
            <Field label="Name"><Input value={name} onChange={(e) => setName(e.target.value)} placeholder="The platform team" /></Field>
            <Field label="Public key (PEM)"><Textarea value={pem} onChange={(e) => setPem(e.target.value)} rows={4} placeholder="-----BEGIN PUBLIC KEY-----" className="font-mono text-xs" /></Field>
            <div><Button size="sm" disabled={!name.trim() || !pem.trim()} loading={add.isPending} onClick={() => add.mutate()}>Trust this key</Button></div>
          </div>
        )}
      </CardContent>
    </Card>
  )
}
