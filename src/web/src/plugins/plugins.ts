import { Blocks, type LucideIcon } from 'lucide-react'
import { api } from '@/lib/api'
import type { NavItem, NavSection } from '@/app/nav'

/** A plugin on for the person, and what it adds to Arena's pages (GET /api/plugins/mine). */
export interface MyPlugin {
  name: string
  title: string
  version: string
  icon: string | null
  dev: boolean
  /** May configure it (its settings screen). */
  manages: boolean
  /** Where its UI files are: an address with its key, for its frames. */
  base: string
  pages: { id: string; title: string; path: string; entry: string; icon: string | null }[]
  menu: { page: string; section: string; title: string | null }[]
  panels: { id: string; title: string; slot: string; entry: string }[]
  settings: string | null
  composerActions: { id: string; title: string; entry: string | null; run: string | null }[]
  messageActions: { id: string; title: string; entry: string | null; run: string | null }[]
  idePanels: { id: string; title: string; entry: string }[]
  writes: string[]
}

export const pluginsQuery = {
  queryKey: ['plugins', 'mine'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<MyPlugin[]>('/api/plugins/mine', { signal }),
  staleTime: 60_000,
}

/** Which of the sidebar's sections a plugin's menu item goes in. */
const SECTION: Record<string, string> = { everyday: 'Everyday', build: 'Build', plugins: 'Plugins', admin: 'Plugins' }

/**
 * The sidebar with the plugins' menu items: each in the section its manifest names (Everyday, Build), or a Plugins
 * section of their own in the workspace. A plugin's page is under /p/<plugin>/<path>.
 */
export function withPluginNav(sections: NavSection[], plugins: MyPlugin[] | undefined, icon: LucideIcon = Blocks): NavSection[] {
  if (!plugins?.length) return sections
  const out = sections.map((s) => ({ ...s, items: [...s.items] }))
  for (const p of plugins) {
    for (const m of p.menu) {
      const page = p.pages.find((x) => x.id === m.page)
      if (!page) continue
      const item: NavItem = { title: m.title ?? page.title, path: `/p/${p.name}/${page.path}`, icon, keywords: ['plugin', p.title, p.name] }
      const title = SECTION[m.section] ?? 'Plugins'
      let section = out.find((s) => s.space === 'work' && s.title === title)
      if (!section) {
        section = { title, space: 'work', items: [] }
        out.push(section)
      }
      section.items.push(item)
    }
  }
  return out
}

/** The page of a plugin a path opens (/p/<plugin>/<page path>/<rest>), and what is left after it. */
export function pageFor(plugins: MyPlugin[] | undefined, plugin: string, rest: string) {
  const p = plugins?.find((x) => x.name === plugin)
  if (!p) return null
  const page = [...p.pages].sort((a, b) => b.path.length - a.path.length).find((x) => rest === x.path || rest.startsWith(`${x.path}/`)) ?? (rest === '' ? p.pages[0] : undefined)
  if (!page) return null
  return { plugin: p, page, rest: rest.slice(page.path.length).replace(/^\//, '') }
}

/** The last answer's text of a chat's turns (what a message action acts on). */
export function lastAnswerOf(turns: { answer: { id: string; role: string; content: string }[] }[]): { id: string; content: string } | null {
  for (let i = turns.length - 1; i >= 0; i--) {
    const a = [...turns[i]!.answer].reverse().find((m) => m.role === 'assistant' && m.content)
    if (a) return { id: a.id, content: a.content }
  }
  return null
}
