import { BarChart3, ImageIcon } from 'lucide-react'
import type { CSSProperties, ReactNode } from 'react'
import { Spinner } from '@/components/ui/spinner'
import { cn } from '@/lib/utils'
import { DeckChartView } from './deck-chart'
import { readableOn, type DeckItem, type DeckLook, type DeckTheme, type Slide } from './decks-api'

/** The built-in look's theme: the app's blue and ink, its six accents. */
const builtIn: Record<string, string> = {
  dk1: '#1B1C1F', lt1: '#FFFFFF', dk2: '#1E3A5F', lt2: '#EEF1F5',
  accent1: '#2A78D6', accent2: '#EB6834', accent3: '#1BAF7A', accent4: '#EDA100', accent5: '#E87BA4', accent6: '#4A3AA7',
}

const rtl = /^[^\p{L}]*[֐-ࣿיִ-ﻼ]/u

/** A line of text, right to left when it reads so. */
function Line({ text, className }: { text: string; className?: string }) {
  return (
    <span dir={rtl.test(text) ? 'rtl' : undefined} className={className}>
      {text}
    </span>
  )
}

function Points({ items, className }: { items: string[]; className?: string }) {
  return (
    <ul className={cn('grid content-start gap-[1.1cqw] text-[2.3cqw] leading-snug', className)}>
      {items.map((b, i) => (
        <li key={i} className="flex gap-[1cqw]">
          <span className="text-[var(--deck-accent)]" aria-hidden="true">
            •
          </span>
          <Line text={b} />
        </li>
      ))}
    </ul>
  )
}

/** An item's heading and words, stacked. */
function Words({ item }: { item: DeckItem }) {
  return (
    <>
      {item.title && <Line text={item.title} className="font-bold" />}
      {item.text && <Line text={item.text} className="text-[var(--deck-muted)]" />}
    </>
  )
}

/**
 * One slide, 16:9, drawn as the PowerPoint file lays it out, in the template's or design's colours and fonts; its sizes
 * follow its width (container units), so a thumbnail and a full screen are the same slide. A thumbnail leaves charts out.
 * With a look and its number: the footer and "n / total" at the bottom, as the file has them.
 */
export function SlideView({ slide, theme, thumbnail, making, className, look, number, total }: {
  slide: Slide; theme?: DeckTheme | null; thumbnail?: boolean; making?: boolean; className?: string; look?: DeckLook | null; number?: number; total?: number
}) {
  const c = { ...builtIn, ...theme?.colors }
  const accents = [1, 2, 3, 4, 5, 6].map((i) => c[`accent${i}`] ?? builtIn[`accent${i}`]!)
  const on = (fill: string) => readableOn(fill, c.dk1!, c.lt1!)
  const style = {
    '--deck-ink': c.dk1,
    '--deck-paper': c.lt1,
    '--deck-title': c.dk2,
    '--deck-muted': c.dk2,
    '--deck-tint': c.lt2,
    '--deck-accent': c.accent1,
    fontFamily: `${theme?.fonts?.minor ? `"${theme.fonts.minor}", ` : ''}Calibri, Carlito, Inter Variable, ui-sans-serif, system-ui, sans-serif`,
  } as CSSProperties
  const heading = { fontFamily: theme?.fonts?.major ? `"${theme.fonts.major}", Calibri, Carlito, Inter Variable, sans-serif` : undefined }
  const title = slide.title ?? ''
  const picture = slide.picture
  const items = slide.items ?? []
  const pictureBox = (box: string) =>
    picture?.fileId ? (
      <img src={`/api/chat/attachments/${picture.fileId}/content`} alt={picture.prompt || title} className={cn('object-contain', box)} />
    ) : picture ? (
      <div className={cn('flex flex-col items-center justify-center gap-[1cqw] rounded-[0.6cqw] bg-[var(--deck-muted)]/8 p-[2cqw] text-center text-[1.5cqw] text-[var(--deck-muted)]', box)}>
        {making ? <Spinner label="Making the picture" /> : <ImageIcon className="size-[4cqw]" aria-hidden="true" />}
        {!thumbnail && <span className="line-clamp-3 italic">{picture.prompt}</span>}
      </div>
    ) : null
  const centred = ['title', 'section', 'quote', 'closing'].includes(slide.layout)
  let body: ReactNode = null
  switch (slide.layout) {
    case 'two':
      body = (
        <>
          <Points items={slide.left ?? []} className="flex-1" />
          <Points items={slide.right ?? []} className="flex-1" />
        </>
      )
      break
    case 'comparison':
      body = [
        { head: slide.leftTitle, points: slide.left ?? [], fill: accents[0]! },
        { head: slide.rightTitle, points: slide.right ?? [], fill: accents[1]! },
      ].map((side, i) => (
        <div key={i} className="flex min-w-0 flex-1 flex-col">
          <div className="px-[1cqw] py-[0.8cqw] text-center text-[2cqw] font-bold" style={{ background: side.fill, color: on(side.fill) }}>
            {side.head ?? ''}
          </div>
          <Points items={side.points} className="flex-1 bg-[var(--deck-tint)] p-[1.2cqw] text-[1.9cqw]" />
        </div>
      ))
      break
    case 'agenda':
      body = (
        <ol className={cn('grid flex-1 content-start gap-x-[3cqw] gap-y-[1.6cqw]', items.length > 4 && 'grid-cols-2')}>
          {items.map((item, i) => (
            <li key={i} className="flex items-center gap-[1.4cqw] text-[2.2cqw]">
              <span className="flex size-[4.2cqw] shrink-0 items-center justify-center rounded-full text-[1.8cqw] font-bold" style={{ background: accents[0], color: on(accents[0]!) }}>
                {i + 1}
              </span>
              <span className="grid">
                <Words item={item} />
              </span>
            </li>
          ))}
        </ol>
      )
      break
    case 'table':
      body = slide.table ? (
        <table className="h-fit w-full border-collapse text-[1.6cqw]">
          <thead>
            <tr>
              {slide.table.columns.map((h, i) => (
                <th key={i} className="px-[1cqw] py-[0.7cqw] text-start font-bold" style={{ background: accents[0], color: on(accents[0]!) }}>
                  {h}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {slide.table.rows.map((r, i) => (
              <tr key={i} className={i % 2 === 0 ? 'bg-[var(--deck-tint)]' : undefined}>
                {r.map((cell, j) => (
                  <td key={j} className="px-[1cqw] py-[0.6cqw]">
                    {cell}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      ) : null
      break
    case 'stats':
      body = items.map((item, i) => (
        <div key={i} className="flex min-w-0 flex-1 flex-col items-center justify-center gap-[1cqw] text-center">
          <span className="text-[6.5cqw] leading-none font-bold" style={{ color: accents[i % 6], ...heading }}>
            {item.value ?? ''}
          </span>
          <span className="grid text-[1.9cqw]">
            <Words item={item} />
          </span>
        </div>
      ))
      break
    case 'cards':
      body = items.map((item, i) => (
        <div key={i} className="flex min-w-0 flex-1 flex-col">
          <div className="h-[0.5cqw]" style={{ background: accents[i % 6] }} />
          <div className="grid flex-1 content-start gap-[0.6cqw] rounded-b-[1cqw] bg-[var(--deck-tint)] p-[1.5cqw] text-[1.8cqw]">
            <Words item={item} />
          </div>
        </div>
      ))
      break
    case 'process':
      body = items.map((item, i) => (
        <div key={i} className="flex min-w-0 flex-1 flex-col gap-[1.5cqw]">
          <div
            className="flex h-[8cqw] items-center justify-center px-[2.5cqw] text-[1.9cqw] font-bold"
            style={{ background: accents[i % 6], color: on(accents[i % 6]!), clipPath: i === 0 ? 'polygon(0 0, 88% 0, 100% 50%, 88% 100%, 0 100%)' : 'polygon(0 0, 88% 0, 100% 50%, 88% 100%, 0 100%, 12% 50%)' }}
          >
            {item.title ?? `Step ${i + 1}`}
          </div>
          {item.text && <Line text={item.text} className="px-[1cqw] text-center text-[1.6cqw]" />}
        </div>
      ))
      break
    case 'timeline':
      body = (
        <div className="relative flex flex-1">
          <div className="absolute inset-x-0 top-[38%] h-[0.3cqw] bg-[var(--deck-muted)]" aria-hidden="true" />
          {items.map((item, i) => (
            <div key={i} className="relative flex min-w-0 flex-1 flex-col items-center text-center">
              <span className="flex h-[36%] items-end pb-[1.2cqw] text-[1.9cqw] font-bold" style={{ color: accents[i % 6] }}>
                {item.title ?? ''}
              </span>
              <span className="size-[2.4cqw] -translate-y-1/2 rounded-full" style={{ background: accents[i % 6] }} aria-hidden="true" />
              {item.text && <Line text={item.text} className="px-[0.8cqw] text-[1.5cqw]" />}
            </div>
          ))}
        </div>
      )
      break
    case 'bullets':
      body = (
        <>
          <Points items={slide.bullets ?? []} className="flex-[56]" />
          {picture && pictureBox('flex-[41] min-w-0 h-full')}
        </>
      )
      break
    case 'chart':
      body = (
        <>
          <div className="relative min-w-0 flex-[66]">
            {slide.chart && slide.chart.categories.length > 0 && slide.chart.series.length > 0 ? (
              thumbnail ? (
                <BarChart3 className="absolute inset-0 m-auto size-[12cqw] text-[var(--deck-accent)]" aria-hidden="true" />
              ) : (
                <DeckChartView chart={slide.chart} accents={accents} ink={c.dk1!} label={title} />
              )
            ) : (
              <p className="absolute inset-0 m-auto h-fit text-center text-[1.8cqw] text-[var(--deck-muted)] italic">{slide.chart?.note ?? 'No numbers yet.'}</p>
            )}
          </div>
          {(slide.bullets?.length ?? 0) > 0 && <Points items={slide.bullets!} className="flex-[30] self-center text-[1.9cqw]" />}
        </>
      )
      break
    case 'picture':
      body = (
        <div className="flex min-w-0 flex-1 flex-col items-center gap-[1.5cqw]">
          {pictureBox('min-h-0 w-full flex-1')}
          {slide.subtitle && <Line text={slide.subtitle} className="text-[1.5cqw] text-[var(--deck-muted)]" />}
        </div>
      )
      break
  }

  return (
    <div
      className={cn('relative aspect-video w-full overflow-hidden bg-[var(--deck-paper)] text-start text-[var(--deck-ink)] [container-type:inline-size]', className)}
      style={style}
    >
      {look?.design === 'bold' && !theme?.width ? (
        <div className="absolute inset-y-0 left-0 w-[2.25%] bg-[var(--deck-accent)]" aria-hidden="true" />
      ) : (
        <div className="absolute inset-x-0 top-0 h-[0.6%] bg-[var(--deck-accent)]" aria-hidden="true" />
      )}
      {(slide.layout === 'title' || slide.layout === 'closing') && (
        <div className="absolute inset-x-[6%] top-[30%] grid gap-[1.6cqw]">
          <h2 className="text-[5cqw] leading-[1.05] font-bold text-[var(--deck-title)]" style={heading}>
            <Line text={title || (slide.layout === 'closing' ? 'Thank you' : '')} />
          </h2>
          <div className="h-[0.35cqw] w-[11%] bg-[var(--deck-accent)]" aria-hidden="true" />
          {slide.subtitle && <Line text={slide.subtitle} className="text-[2.5cqw] text-[var(--deck-muted)]" />}
          {slide.layout === 'closing' && (slide.bullets ?? []).map((b, i) => <Line key={i} text={b} className="text-[2cqw] text-[var(--deck-muted)]" />)}
        </div>
      )}
      {slide.layout === 'section' && (
        <div className="absolute inset-x-[6%] top-[34%] grid gap-[1.6cqw]">
          <h2 className="text-[4.6cqw] leading-tight font-bold text-[var(--deck-title)]" style={heading}>
            <Line text={title} />
          </h2>
          <div className="h-[0.35cqw] w-[11%] bg-[var(--deck-accent)]" aria-hidden="true" />
          {slide.subtitle && <Line text={slide.subtitle} className="text-[2.1cqw] text-[var(--deck-muted)]" />}
        </div>
      )}
      {slide.layout === 'quote' && (
        <figure className="absolute inset-x-[10%] inset-y-[18%] flex flex-col items-center justify-center gap-[2cqw] text-center">
          <blockquote className="text-[3.3cqw] leading-snug italic">
            <Line text={`“${slide.quote ?? title}”`} />
          </blockquote>
          {slide.by && <figcaption className="text-[1.9cqw] text-[var(--deck-muted)]">— {slide.by}</figcaption>}
        </figure>
      )}
      {!centred && (
        <>
          <h2 className="absolute inset-x-[6%] top-[5%] flex h-[16%] items-end text-[3.75cqw] leading-tight font-bold text-[var(--deck-title)]" style={heading}>
            <Line text={title} className="line-clamp-2" />
          </h2>
          <div className="absolute inset-x-[6%] top-[25%] bottom-[8%] flex gap-[3cqw]">{body}</div>
        </>
      )}
      {look && number && total && slide.layout !== 'title' && slide.layout !== 'closing' && (
        <div className="absolute inset-x-[6%] bottom-[2%] flex justify-between text-[1.1cqw] text-[var(--deck-muted)]">
          <span>{look.footer ?? ''}</span>
          {look.numbers && <span>{`${number} / ${total}`}</span>}
        </div>
      )}
    </div>
  )
}
