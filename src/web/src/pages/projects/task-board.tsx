import { Badge } from '@/components/ui/badge'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { cn } from '@/lib/utils'
import { taskStatusLabels, type ProjectGoal, type ProjectTask, type TaskStatus } from './api'

const columns: TaskStatus[] = ['todo', 'doing', 'blocked', 'done']

/** When a task is due, and whether that has passed while it is not done. */
export function Due({ task }: { task: ProjectTask }) {
  if (!task.dueOn) return null
  const late = !task.done && task.dueOn < new Date().toISOString().slice(0, 10)
  return <span className={cn('text-xs tabular-nums', late ? 'text-destructive-ink' : 'text-muted-foreground')}>{late ? `late: due ${task.dueOn}` : `due ${task.dueOn}`}</span>
}

/** Where a task stands, to change. */
export function StatusSelect({ task, disabled, onChange }: { task: ProjectTask; disabled: boolean; onChange: (s: TaskStatus) => void }) {
  return (
    <Select value={task.status} disabled={disabled} onValueChange={(v) => onChange(v as TaskStatus)}>
      <SelectTrigger className="h-8 w-28" aria-label={`Where ${task.title} stands`}>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        {columns.map((s) => (
          <SelectItem key={s} value={s}>
            {taskStatusLabels[s]}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  )
}

/** The project's tasks as a board: a column for each status, each task with its goal, who does it, when and what it waits for. */
export function TaskBoard({ tasks, goals, may, onStatus }: { tasks: ProjectTask[]; goals: ProjectGoal[]; may: boolean; onStatus: (t: ProjectTask, s: TaskStatus) => void }) {
  const titles = new Map(tasks.map((t) => [t.id, t.title]))
  return (
    <div className="grid gap-3 sm:grid-cols-2 2xl:grid-cols-4" aria-label="Board">
      {columns.map((c) => {
        const here = tasks.filter((t) => t.status === c)
        return (
          <section key={c} aria-label={taskStatusLabels[c]} className="grid content-start gap-2 rounded-lg bg-muted/50 p-2">
            <h3 className="px-1 text-xs font-medium text-muted-foreground">
              {taskStatusLabels[c]} ({here.length})
            </h3>
            {here.map((t) => (
              <article key={t.id} aria-label={t.title} className="grid gap-1.5 rounded-md border bg-card p-2 text-sm">
                <bdi className={cn('font-medium', t.done && 'text-muted-foreground line-through')}>{t.title}</bdi>
                <div className="flex flex-wrap items-center gap-1.5">
                  {t.goalId && <Badge variant="outline">{goals.find((g) => g.id === t.goalId)?.title}</Badge>}
                  {(t.assignee ?? t.owner) && <span className="text-xs text-muted-foreground">{t.assignee ?? t.owner}</span>}
                  <Due task={t} />
                </div>
                {(t.dependsOn ?? []).length > 0 && (
                  <p className="text-xs text-muted-foreground">Waits for {(t.dependsOn ?? []).map((d) => titles.get(d) ?? 'a removed task').join(', ')}</p>
                )}
                {t.notes && <p className="line-clamp-3 text-xs text-muted-foreground">{t.notes}</p>}
                <StatusSelect task={t} disabled={!may} onChange={(s) => onStatus(t, s)} />
              </article>
            ))}
          </section>
        )
      })}
    </div>
  )
}
