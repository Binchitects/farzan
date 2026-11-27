// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
import { api, ApiError } from '@/lib/api'

/**
 * The page's side of a plugin's frame (the framework's @farzan-arena/plugin-ui speaks the other): the frame is sandboxed
 * (an opaque origin, no network of its own), so it asks the page, by postMessage, and the page answers only the frame it
 * made, only within the limits here, and only through Arena's API as the person looking at it, which checks the
 * plugin's grants as it does for the plugin's server. Nothing of the page (its cookies, its storage, its DOM) is the
 * frame's to reach.
 *
 *   frame -> page   { arena: 1, id, method, params }
 *   page -> frame   { arena: 1, id, result } | { arena: 1, id, error: { code, message } }
 *   page -> frame   { arena: 1, event, data }
 */

export const BRIDGE_VERSION = 1
/** Calls a second at most, as a bucket: a burst of so many, then so many a second. */
export const BURST = 100
export const PER_SECOND = 50
/** Refusals past the limit after which the frame is closed with its error card. */
export const MOST_REFUSED = 500
/** A frame that has not said hello by then is shown as not loading. */
export const LOAD_DEADLINE_MS = 8000
export const MIN_HEIGHT = 48
export const MAX_HEIGHT = 4000

/** The review an IDE panel is shown beside: its repository, its merge request (or branches), its title. */
export interface FrameReview {
  id: string
  project: string
  title: string
  mergeRequest: number | null
  fromRef: string | null
  toRef: string | null
}

export interface FrameContext {
  plugin: { name: string; version: string; title: string }
  slot: 'page' | 'panel' | 'settings' | 'composer-action' | 'message-action' | 'ide-panel'
  id: string
  rest: string
  person: { id: string; name: string }
  chatId: string | null
  projectId: string | null
  message: { id: string; content: string } | null
  /** For an IDE panel beside a review (Code → a review): the review; null on the Code page itself. */
  review: FrameReview | null
  theme: { mode: 'light' | 'dark'; tokens: Record<string, string> }
  locale: string
  dev: boolean
}

export interface BridgeHost {
  /** Arena's API (fetch through the app's session); a test gives its own. */
  call?: typeof api
  navigate(path: string): void
  toast(text: string, kind: 'info' | 'success' | 'error'): void
  resize(height: number): void
  close(): void
  insert(text: string): void
  /** Asks the person before a tool that changes something runs; true: yes. */
  confirm(title: string, body: string): Promise<boolean>
  /** The frame broke the bridge's limits: its card says why. */
  broke(why: string): void
  hello(): void
  now?: () => number
}

export interface BridgeOptions {
  plugin: string
  context: FrameContext
  /** The plugin's functions that change something: a call asks the person first. */
  writes: string[]
  host: BridgeHost
}

type Msg = { arena?: unknown; id?: unknown; method?: unknown; params?: unknown }

class BridgeError extends Error {
  readonly code: number
  constructor(code: number, message: string) {
    super(message)
    this.code = code
  }
}

/** Where a frame may send the person: one of its own plugin's pages, a chat or a project; nowhere else, never out of Arena. */
export function allowedPath(plugin: string, path: string): boolean {
  if (typeof path !== 'string' || !path.startsWith('/') || path.startsWith('//') || path.includes('\\') || [...path].some((c) => c.charCodeAt(0) < 32)) return false
  const clean = path.split(/[?#]/)[0]!
  if (clean.split('/').some((p) => p === '..' || p === '.')) return false
  const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'
  return clean === `/p/${plugin}` || clean.startsWith(`/p/${plugin}/`) || new RegExp(`^/(chat|projects)/${uuid}$`).test(clean)
}

/**
 * Answers one frame's messages. Returns the handler to give a message event to, after checking it came from that frame's
 * window (the caller compares event.source): the bridge itself never trusts an origin, an opaque frame has none.
 */
export function createBridge(o: BridgeOptions, reply: (message: unknown) => void) {
  const call = o.host.call ?? api
  const now = o.host.now ?? (() => Date.now())
  let tokens = BURST
  let last = now()
  let refused = 0
  let greeted = false
  let closed = false
  const enc = encodeURIComponent(o.plugin)

  const take = () => {
    const t = now()
    tokens = Math.min(BURST, tokens + ((t - last) / 1000) * PER_SECOND)
    last = t
    if (tokens < 1) return false
    tokens -= 1
    return true
  }

  const answer = async (method: string, params: Record<string, unknown>): Promise<unknown> => {
    switch (method) {
      case 'hello':
        if (!greeted) {
          greeted = true
          o.host.hello()
        }
        return o.context
      case 'ui.navigate': {
        const path = String(params.path ?? '')
        if (!allowedPath(o.plugin, path)) throw new BridgeError(-32001, `A plugin may open its own pages, a chat or a project, not ${path.slice(0, 100)}.`)
        o.host.navigate(path)
        return null
      }
      case 'ui.toast':
        o.host.toast(String(params.text ?? '').slice(0, 300), params.kind === 'success' || params.kind === 'error' ? params.kind : 'info')
        return null
      case 'ui.resize': {
        const h = Number(params.height)
        if (Number.isFinite(h)) o.host.resize(Math.max(MIN_HEIGHT, Math.min(MAX_HEIGHT, Math.round(h))))
        return null
      }
      case 'ui.close':
        o.host.close()
        return null
      case 'ui.insert':
        if (o.context.slot !== 'composer-action') throw new BridgeError(-32001, 'Only a composer action puts text in the composer.')
        o.host.insert(String(params.text ?? '').slice(0, 10000))
        return null
      case 'plugin.route': {
        const m = String(params.method ?? 'GET').toUpperCase()
        const path = String(params.path ?? '/')
        if (!['GET', 'POST', 'PUT', 'PATCH', 'DELETE'].includes(m) || !path.startsWith('/') || path.split(/[?#]/)[0]!.split('/').some((p) => p === '..' || p === '.')) {
          throw new BridgeError(-32602, 'A route is a method and a path of the plugin\'s own.')
        }
        const res = await fetch(`/api/p/${enc}${path}`, {
          method: m, credentials: 'same-origin',
          headers: { Accept: 'application/json', 'X-Requested-With': 'fetch', 'X-Arena-Bridge': '1', ...(params.body == null ? {} : { 'Content-Type': 'application/json' }) },
          body: params.body == null || m === 'GET' ? undefined : String(params.body),
        })
        const headers: Record<string, string> = {}
        const type = res.headers.get('content-type')
        if (type) headers['content-type'] = type
        return { status: res.status, headers, body: await res.text() }
      }
      case 'plugin.tool': {
        const name = String(params.name ?? '')
        let confirmed = false
        if (o.writes.includes(name)) {
          confirmed = await o.host.confirm(`${o.context.plugin.title} wants to run ${name}`, 'It changes something. Run it?')
          if (!confirmed) return { content: 'The person did not allow it.', isError: true }
        }
        return await call(`/api/plugins/${enc}/tools/${encodeURIComponent(name)}`, {
          body: { arguments: params.arguments ?? {}, confirmed, chatId: o.context.chatId, projectId: o.context.projectId },
          headers: { 'X-Arena-Bridge': '1' },
        })
      }
      default: {
        if (method.startsWith('ui.') || method.startsWith('plugin.')) throw new BridgeError(-32601, `There is no ${method}.`)
        const r = await call<{ result?: unknown; error?: { code: number; message: string } }>(`/api/plugins/${enc}/host`, {
          body: { method, params, chatId: o.context.chatId, projectId: o.context.projectId },
          headers: { 'X-Arena-Bridge': '1' },
        })
        if (r.error) throw new BridgeError(r.error.code, r.error.message)
        return r.result ?? null
      }
    }
  }

  return {
    get greeted() {
      return greeted
    },
    close() {
      closed = true
    },
    /** One message from the frame's window. */
    async receive(data: unknown) {
      if (closed) return
      const m = data as Msg
      if (!m || typeof m !== 'object' || m.arena !== BRIDGE_VERSION || typeof m.method !== 'string' || (typeof m.id !== 'number' && typeof m.id !== 'string')) return
      const id = m.id
      if (!take()) {
        refused++
        reply({ arena: BRIDGE_VERSION, id, error: { code: -32003, message: 'Too many calls: the page answers at most 50 a second.' } })
        if (refused >= MOST_REFUSED) {
          closed = true
          o.host.broke('It sent the page far more calls than it may, so it was stopped.')
        }
        return
      }
      if (m.method !== 'hello' && !greeted) {
        reply({ arena: BRIDGE_VERSION, id, error: { code: -32002, message: 'Say hello first (connect()).' } })
        return
      }
      try {
        const params = m.params && typeof m.params === 'object' && !Array.isArray(m.params) ? (m.params as Record<string, unknown>) : {}
        const result = await answer(m.method, params)
        if (!closed) reply({ arena: BRIDGE_VERSION, id, result: result ?? null })
      } catch (e) {
        const code = e instanceof BridgeError ? e.code : e instanceof ApiError ? (e.http === 404 ? -32002 : e.http === 403 ? -32001 : -32603) : -32603
        if (!closed) reply({ arena: BRIDGE_VERSION, id, error: { code, message: e instanceof Error ? e.message : String(e) } })
      }
    },
    /** Tell the frame something changed (the chat it is beside, the theme). */
    event(event: 'context' | 'theme' | 'visible', data: unknown) {
      if (!closed) reply({ arena: BRIDGE_VERSION, event, data })
    },
  }
}
