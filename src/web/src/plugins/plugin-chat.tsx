import { useQuery } from '@tanstack/react-query'
import { Blocks } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { useConfirm } from '@/components/ui/confirm'
import { Sheet, SheetContent, SheetDescription, SheetTitle } from '@/components/ui/sheet'
import { toast } from '@/components/ui/toaster'
import { api, errorMessage } from '@/lib/api'
import { PluginFrame } from './plugin-frame'
import { PluginPanels } from './plugin-page'
import { pluginsQuery, type MyPlugin } from './plugins'


/**
 * The plugins beside a chat: their panels (chat.sidebar), their composer actions (a frame whose text goes to the
 * clipboard, to paste into the message) and their message actions (a function run with the last answer's text, as
 * { text }), in one sheet. Shown only when some plugin on for the person brings any.
 */
export function PluginChatButton({ chatId, lastAnswer }: { chatId: string | null | undefined; lastAnswer?: { id: string; content: string } | null }) {
  const plugins = useQuery(pluginsQuery)
  const [open, setOpen] = useState(false)
  const [action, setAction] = useState<{ plugin: MyPlugin; id: string; entry: string } | null>(null)
  const confirm = useConfirm()
  const list = plugins.data ?? []
  const panels = list.some((p) => p.panels.some((x) => x.slot === 'chat.sidebar'))
  const composer = list.flatMap((p) => p.composerActions.filter((a) => a.entry).map((a) => ({ plugin: p, action: a })))
  const message = list.flatMap((p) => p.messageActions.map((a) => ({ plugin: p, action: a })))
  if (!panels && composer.length === 0 && message.length === 0) return null

  const run = async (p: MyPlugin, a: MyPlugin['messageActions'][number]) => {
    if (!lastAnswer) return
    if (a.entry) {
      setAction({ plugin: p, id: a.id, entry: a.entry })
      return
    }
    const fn = a.run?.replace(/^tool:/, '') ?? ''
    const writes = p.writes.includes(fn)
    if (writes && !(await confirm({ title: `${p.title}: ${a.title}`, description: 'It changes something. Run it?', confirm: 'Run it' }))) return
    try {
      const r = await api<{ content: string; isError: boolean }>(`/api/plugins/${encodeURIComponent(p.name)}/tools/${encodeURIComponent(fn)}`, {
        body: { arguments: { text: lastAnswer.content }, confirmed: writes, chatId: chatId ?? null }, headers: { 'X-Arena-Bridge': '1' },
      })
      ;(r.isError ? toast.error : toast.success)(r.content.slice(0, 300))
    } catch (e) {
      toast.error(errorMessage(e))
    }
  }

  return (
    <>
      <Button type="button" variant="ghost" size="icon-sm" aria-label="Plugins" title="Plugins" onClick={() => setOpen(true)}>
        <Blocks className="size-4" aria-hidden />
      </Button>
      <Sheet open={open} onOpenChange={(o) => { setOpen(o); if (!o) setAction(null) }}>
        <SheetContent side="right" className="gap-3 overflow-y-auto p-4 sm:max-w-md">
          <SheetTitle>Plugins</SheetTitle>
          <SheetDescription>What the plugins on for you add to this chat.</SheetDescription>
          {message.length > 0 && (
            <div className="flex flex-wrap gap-2" aria-label="With the last answer">
              {message.map(({ plugin, action: a }) => (
                <Button key={`${plugin.name}:${a.id}`} size="sm" variant="outline" disabled={!lastAnswer} onClick={() => void run(plugin, a)}>{a.title}</Button>
              ))}
            </div>
          )}
          {composer.length > 0 && (
            <div className="flex flex-wrap gap-2" aria-label="For your message">
              {composer.map(({ plugin, action: a }) => (
                <Button key={`${plugin.name}:${a.id}`} size="sm" variant="outline" onClick={() => setAction({ plugin, id: a.id, entry: a.entry! })}>{a.title}</Button>
              ))}
            </div>
          )}
          {action && (
            <PluginFrame plugin={action.plugin} slot="composer-action" id={action.id} entry={action.entry} chatId={chatId ?? null}
              message={lastAnswer ?? null}
              onInsert={(text) => { void navigator.clipboard?.writeText(text).then(() => toast.success('Copied: paste it into your message.'), () => toast(text)) }}
              onClose={() => setAction(null)} className="rounded-md border" />
          )}
          {panels && <PluginPanels slot="chat.sidebar" chatId={chatId ?? null} />}
        </SheetContent>
      </Sheet>
    </>
  )
}
