import { useQuery } from '@tanstack/react-query'
import { Blocks } from 'lucide-react'
import { useParams } from 'react-router'
import { PageHeader } from '@/components/app/page-header'
import { PageSkeleton, QueryError } from '@/components/app/query-state'
import { Badge } from '@/components/ui/badge'
import { EmptyState } from '@/components/ui/empty-state'
import { PluginFrame } from './plugin-frame'
import { pageFor, pluginsQuery } from './plugins'

/** A plugin's page (/p/<plugin>/<page>/<rest>): its frame, under Arena's header, nothing of Arena's inside it. */
export function PluginPage() {
  const { plugin = '', '*': rest = '' } = useParams()
  const plugins = useQuery(pluginsQuery)
  if (plugins.isPending) return <PageSkeleton />
  if (plugins.isError) return <QueryError error={plugins.error} retry={() => void plugins.refetch()} />
  const found = pageFor(plugins.data, plugin, rest)
  if (!found) {
    return <EmptyState icon={Blocks} title="No such plugin page">The plugin is not installed, not on for you, or has no page here.</EmptyState>
  }
  return (
    <div className="flex h-full flex-col gap-3">
      <PageHeader title={found.page.title} description={<span className="flex items-center gap-2">{found.plugin.title} {found.plugin.dev && <Badge variant="outline">dev</Badge>}</span>} />
      <PluginFrame plugin={found.plugin} slot="page" id={found.page.id} entry={found.page.entry} rest={found.rest} fill className="flex-1 rounded-md border" />
    </div>
  )
}

/** The plugins' panels for a slot (beside a chat, a project, on the home page), each in its own frame. */
export function PluginPanels({ slot, chatId = null, projectId = null }: { slot: string; chatId?: string | null; projectId?: string | null }) {
  const plugins = useQuery(pluginsQuery)
  const panels = (plugins.data ?? []).flatMap((p) => p.panels.filter((x) => x.slot === slot).map((x) => ({ plugin: p, panel: x })))
  if (panels.length === 0) return null
  return (
    <div className="flex flex-col gap-3" data-plugin-slot={slot}>
      {panels.map(({ plugin, panel }) => (
        <section key={`${plugin.name}:${panel.id}`} aria-label={panel.title} className="rounded-md border">
          <h3 className="border-b px-3 py-1.5 text-xs font-medium text-muted-foreground">{panel.title}</h3>
          <PluginFrame plugin={plugin} slot="panel" id={panel.id} entry={panel.entry} chatId={chatId} projectId={projectId} />
        </section>
      ))}
    </div>
  )
}
