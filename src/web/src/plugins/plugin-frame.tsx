import { useQuery } from '@tanstack/react-query'
import { AlertTriangle, RotateCw } from 'lucide-react'
import { Component, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useNavigate } from 'react-router'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { toast } from '@/components/ui/toaster'
import { meQuery } from '@/lib/api'
import { useTheme } from '@/lib/theme'
import { cn } from '@/lib/utils'
import { createBridge, LOAD_DEADLINE_MS, type FrameContext } from './plugin-bridge'
import type { MyPlugin } from './plugins'

/** What a frame shows when its plugin broke: what happened, and a way to try again. The page around it goes on. */
function FrameCard({ title, why, onRetry, className }: { title: string; why: string; onRetry?: () => void; className?: string }) {
  return (
    <div role="alert" data-plugin-error className={cn('flex flex-col items-start gap-2 rounded-md border border-dashed p-4 text-sm', className)}>
      <div className="flex items-center gap-2 font-medium"><AlertTriangle className="size-4 text-amber-600" aria-hidden /> {title} is not working</div>
      <p className="text-muted-foreground">{why}</p>
      {onRetry && <Button size="sm" variant="outline" onClick={onRetry}><RotateCw className="size-3.5" aria-hidden /> Try again</Button>}
    </div>
  )
}

/** Catches what goes wrong around a frame in this page (its context, its host): the page beside it is never taken down. */
export class PluginErrorBoundary extends Component<{ title: string; children: ReactNode }, { error: Error | null }> {
  override state = { error: null as Error | null }
  static getDerivedStateFromError(error: Error) {
    return { error }
  }
  override render() {
    return this.state.error
      ? <FrameCard title={this.props.title} why={this.state.error.message} onRetry={() => this.setState({ error: null })} />
      : this.props.children
  }
}

export interface PluginFrameProps {
  plugin: MyPlugin
  slot: FrameContext['slot']
  /** The page's or panel's id. */
  id: string
  entry: string
  rest?: string
  chatId?: string | null
  projectId?: string | null
  message?: { id: string; content: string } | null
  /** Fill the space (a page) or follow the frame's height (a panel). */
  fill?: boolean
  onClose?: () => void
  onInsert?: (text: string) => void
  className?: string
}

/**
 * A plugin's page or panel: its own sandboxed frame (allow-scripts, never allow-same-origin: an opaque origin with no
 * cookies, storage or Arena of its own), answered by the bridge; inside an error boundary; shown as not working when it
 * does not say hello by its deadline, or breaks the bridge's limits.
 */
export function PluginFrame(props: PluginFrameProps) {
  return (
    <PluginErrorBoundary title={props.plugin.title}>
      <Frame {...props} />
    </PluginErrorBoundary>
  )
}

function Frame({ plugin, slot, id, entry, rest = '', chatId = null, projectId = null, message = null, fill, onClose, onInsert, className }: PluginFrameProps) {
  const me = useQuery(meQuery)
  const { resolved } = useTheme()
  const navigate = useNavigate()
  const confirm = useConfirm()
  const frame = useRef<HTMLIFrameElement>(null)
  const [height, setHeight] = useState(fill ? 0 : 160)
  const [broke, setBroke] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)
  const context = useMemo<FrameContext>(() => ({
    plugin: { name: plugin.name, version: plugin.version, title: plugin.title }, slot, id, rest,
    person: { id: me.data?.id ?? '', name: me.data?.displayName || me.data?.userName || '' },
    chatId, projectId, message, theme: { mode: resolved, tokens: themeTokens() }, locale: navigator.language || 'en', dev: plugin.dev,
  }), [plugin, slot, id, rest, me.data, chatId, projectId, message, resolved])
  const contextRef = useRef(context)
  const bridgeRef = useRef<ReturnType<typeof createBridge> | null>(null)

  useEffect(() => {
    const bridge = createBridge({
      plugin: plugin.name, context: contextRef.current, writes: plugin.writes,
      host: {
        navigate: (path) => navigate(path),
        toast: (text, kind) => (kind === 'error' ? toast.error(text) : kind === 'success' ? toast.success(text) : toast(text)),
        resize: (h) => { if (!fill) setHeight(h) },
        close: () => onClose?.(),
        insert: (text) => onInsert?.(text),
        confirm: (title, body) => confirm({ title, description: body, confirm: 'Run it' }),
        broke: (why) => setBroke(why),
        hello: () => {},
      },
    }, (m) => frame.current?.contentWindow?.postMessage(m, '*'))
    bridgeRef.current = bridge
    const listener = (e: MessageEvent) => {
      // Only this frame's window: another frame, a popup or the page itself is not this plugin.
      if (!frame.current || e.source !== frame.current.contentWindow) return
      void bridge.receive(e.data)
    }
    window.addEventListener('message', listener)
    const deadline = window.setTimeout(() => {
      if (!bridge.greeted) setBroke(`It did not start within ${LOAD_DEADLINE_MS / 1000} seconds: its page may have failed as it loaded.`)
    }, LOAD_DEADLINE_MS)
    return () => {
      bridge.close()
      window.removeEventListener('message', listener)
      window.clearTimeout(deadline)
    }
  }, [plugin, attempt, navigate, confirm, fill, onClose, onInsert])

  // The context it is shown in changed (another chat, the theme): told, not reloaded.
  useEffect(() => {
    contextRef.current = context
    bridgeRef.current?.event('context', context)
  }, [context])

  if (broke) return <FrameCard title={plugin.title} why={broke} onRetry={() => { setBroke(null); setAttempt((a) => a + 1) }} className={className} />
  return (
    <iframe
      key={attempt}
      ref={frame}
      title={`${plugin.title}${plugin.dev ? ' (dev)' : ''}`}
      src={plugin.base + entry}
      // Scripts and forms only: no same origin (so no cookies, storage or Arena), no popups (they would carry what it read to any site),
      // no top navigation, no downloads. The page's own policy (frame-src 'self') refuses the frame navigating itself off Arena.
      sandbox="allow-scripts allow-forms allow-modals"
      referrerPolicy="no-referrer"
      loading="lazy"
      data-plugin={plugin.name}
      data-slot={slot}
      className={cn('w-full border-0 bg-transparent', fill && 'h-full min-h-[60vh]', plugin.dev && 'outline outline-2 outline-dashed outline-amber-500', className)}
      style={fill ? undefined : { height }}
    />
  )
}

/** The page's theme as CSS custom properties a plugin's frame may use (--arena-bg, --arena-fg, --arena-accent, --arena-border). */
function themeTokens(): Record<string, string> {
  if (typeof document === 'undefined') return {}
  const s = getComputedStyle(document.documentElement)
  const pick = (name: string) => s.getPropertyValue(name).trim()
  const tokens: Record<string, string> = {}
  for (const [k, v] of [['--arena-bg', '--background'], ['--arena-fg', '--foreground'], ['--arena-accent', '--primary'], ['--arena-border', '--border'], ['--arena-muted', '--muted-foreground']] as const) {
    const value = pick(v)
    if (value) tokens[k] = value.startsWith('#') || value.includes('(') ? value : `hsl(${value})`
  }
  return tokens
}
