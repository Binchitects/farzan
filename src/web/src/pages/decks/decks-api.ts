import { api } from '@/lib/api'
import type { DataResult } from '../data/data-api'

export type Layout =
  | 'title' | 'section' | 'agenda' | 'bullets' | 'two' | 'comparison' | 'chart' | 'table' | 'timeline' | 'process' | 'stats' | 'cards' | 'picture' | 'quote' | 'closing'

export type ChartType = 'bar' | 'hbar' | 'stacked' | 'line' | 'area' | 'pie' | 'doughnut' | 'scatter'

/** One of a slide's items: an agenda's or timeline's step, a process step, a card, a big number. */
export interface DeckItem {
  title?: string | null
  text?: string | null
  /** A big number's figure, as written ("42%"). */
  value?: string | null
}

/** A deck's look: a built-in design (without a company template), its footer, slide numbers and transition. */
export interface DeckLook {
  design: string
  footer?: string | null
  numbers: boolean
  transition: 'none' | 'fade' | 'push' | 'wipe'
}

export interface DeckChart {
  type: ChartType
  categories: string[]
  series: { name: string; values: (number | null)[] }[]
  /** The question asked of a database for its numbers. */
  ask?: { source: string; question: string } | null
  /** Where its numbers came from, or why there are none. */
  note?: string | null
}

export interface Slide {
  layout: Layout
  title?: string | null
  subtitle?: string | null
  bullets?: string[] | null
  left?: string[] | null
  right?: string[] | null
  leftTitle?: string | null
  rightTitle?: string | null
  items?: DeckItem[] | null
  table?: { columns: string[]; rows: string[][] } | null
  quote?: string | null
  by?: string | null
  chart?: DeckChart | null
  picture?: { prompt: string; fileId?: string | null } | null
  notes?: string | null
  /** The slide of the PowerPoint file it was read from (a deck opened to revise). */
  origin?: { slide: number; read?: string | null } | null
}

export interface Deck {
  id: string
  title: string
  templateId: string | null
  look: DeckLook
  /** The PowerPoint file it was opened from, to revise; null for a deck made here. */
  source?: string | null
  version: number
  createdAt: string
  updatedAt: string
  slides: Slide[]
}

export interface DeckBrief {
  id: string
  title: string
  templateId: string | null
  look: DeckLook
  version: number
  updatedAt: string
  slides: number
  first: Slide | null
}

/** A template's look as the browser draws it: its theme's colours and fonts. */
export interface DeckTheme {
  colors?: Record<string, string>
  fonts?: { major?: string | null; minor?: string | null }
  width?: number
  height?: number
}

export interface DeckTemplate {
  id: string
  name: string
  isDefault: boolean
  theme: DeckTheme
}

export interface DeckConfig {
  pictures: boolean
  templates: DeckTemplate[]
  sources: string[]
  /** The built-in designs, each with its theme as the browser draws it. */
  designs: { name: string; theme: DeckTheme }[]
}

export interface DeckVersion {
  version: number
  title: string
  note: string
  createdAt: string
}

export const decksQuery = {
  queryKey: ['decks'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<DeckBrief[]>('/api/decks', { signal }),
}

export const deckConfigQuery = {
  queryKey: ['decks', 'config'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<DeckConfig>('/api/decks/config', { signal }),
}

export const deckQuery = (id: string) => ({
  queryKey: ['decks', id] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<Deck>(`/api/decks/${id}`, { signal }),
})

export const deckVersionsQuery = (id: string) => ({
  queryKey: ['decks', id, 'versions'] as const,
  queryFn: ({ signal }: { signal: AbortSignal }) => api<DeckVersion[]>(`/api/decks/${id}/versions`, { signal }),
})

export const makeDeck = (body: { about?: string; templateId?: string; look?: DeckLook; fileId?: string; canvasId?: string }) => api<Deck>('/api/decks', { body })

/** A PowerPoint file of one's own opened as a deck, to revise: it comes back as that file, only the changed slides made anew. */
export const importDeck = (file: File) => {
  const form = new FormData()
  form.append('file', file)
  return api<Deck>('/api/decks/import', { body: form })
}

export const changeDeck = (id: string, body: { change: string; slide?: number }) => api<Deck>(`/api/decks/${id}/ask`, { body })

export const editDeck = (id: string, body: { title?: string; slides?: Slide[]; templateId?: string; note?: string; look?: DeckLook }) => api<Deck>(`/api/decks/${id}`, { method: 'PUT', body })

export const removeDeck = (id: string) => api(`/api/decks/${id}`, { method: 'DELETE' })

export const makePicture = (id: string, index: number) => api<Deck>(`/api/decks/${id}/slides/${index}/picture`, { body: {} })

export const refreshDeck = (id: string) => api<Deck>(`/api/decks/${id}/refresh`, { body: {} })

export const restoreDeck = (id: string, version: number) => api<Deck>(`/api/decks/${id}/versions/${version}/restore`, { body: {} })

/** A deck's theme as the browser draws it: its template's, else its built-in design's. */
export function themeFor(config: DeckConfig | undefined, templateId: string | null, look: DeckLook | undefined) {
  return templateId ? config?.templates.find((t) => t.id === templateId)?.theme : config?.designs.find((d) => d.name === (look?.design ?? 'arena'))?.theme
}

/** The built-in look's template id, when choosing one. */
export const BuiltIn = '00000000-0000-0000-0000-000000000000'

export const layoutLabels: Record<Layout, string> = {
  title: 'Title',
  section: 'Section',
  agenda: 'Agenda',
  bullets: 'Points',
  two: 'Two columns',
  comparison: 'Comparison',
  chart: 'Chart',
  table: 'Table',
  stats: 'Big numbers',
  cards: 'Cards',
  process: 'Process',
  timeline: 'Timeline',
  picture: 'Picture',
  quote: 'Quote',
  closing: 'Closing',
}

export const chartLabels: Record<ChartType, string> = {
  bar: 'Bars',
  hbar: 'Bars across',
  stacked: 'Stacked bars',
  line: 'A line',
  area: 'Areas',
  pie: 'A pie',
  doughnut: 'A doughnut',
  scatter: 'Points (x, y)',
}

export const designLabels: Record<string, string> = { arena: 'Arena', midnight: 'Midnight (dark)', paper: 'Paper', bold: 'Bold', minimal: 'Minimal' }

/** How many items each layout of items shows at most, as the API keeps them. */
export const mostItems: Partial<Record<Layout, number>> = { stats: 4, cards: 4, process: 5, timeline: 6, agenda: 8 }

export const defaultLook: DeckLook = { design: 'arena', footer: null, numbers: true, transition: 'none' }

/** A deck chart as a query result, for the data chart: a column of names, a column for each series. */
export function chartResult(chart: DeckChart): DataResult {
  return {
    columns: [{ name: 'Category', type: 'text' }, ...chart.series.map((s) => ({ name: s.name, type: 'number' as const }))],
    rows: chart.categories.map((c, i) => [c, ...chart.series.map((s) => s.values[i] ?? null)]),
    truncated: false,
    milliseconds: 0,
  }
}

/** WCAG's relative luminance of a #RRGGBB colour. */
function luminance(hex: string) {
  const c = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255).map((v) => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4))
  return 0.2126 * c[0]! + 0.7152 * c[1]! + 0.0722 * c[2]!
}

/** The theme's dark words or its paper, whichever reads on a colour (as the PowerPoint file chooses). */
export function readableOn(fill: string, ink: string, paper: string) {
  const ratio = (a: number, b: number) => (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05)
  const l = luminance(fill)
  return ratio(l, luminance(ink)) > ratio(l, luminance(paper)) ? ink : paper
}
