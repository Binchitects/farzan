import { BarChart, LineChart, PieChart, ScatterChart } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import * as echarts from 'echarts/core'
import { SVGRenderer } from 'echarts/renderers'
import { useEffect, useRef } from 'react'
import { formatValue } from '@/lib/format'
import type { DeckChart } from './decks-api'

echarts.use([BarChart, LineChart, PieChart, ScatterChart, GridComponent, TooltipComponent, LegendComponent, SVGRenderer])

/**
 * A deck's chart as the PowerPoint file draws it: in the deck's six accent colours, bars up or across, stacked, a line,
 * areas, a pie or doughnut (each slice its colour, as shares) or points (the categories as x numbers); a legend for more
 * than one series, or for a pie's slices.
 */
export function DeckChartView({ chart, accents, ink, label }: { chart: DeckChart; accents: string[]; ink: string; label: string }) {
  const ref = useRef<HTMLDivElement>(null)
  // The colours by value: a new list of the same colours draws nothing again.
  const palette = accents.join(',')
  useEffect(() => {
    const el = ref.current
    if (!el) return
    const plot = echarts.init(el, undefined, { renderer: 'svg' })
    const round = chart.type === 'pie' || chart.type === 'doughnut'
    const across = chart.type === 'hbar'
    // Its words scale with it, as the slide's do: a thumbnail and a full screen are the same chart.
    const draw = () => {
      const size = Math.max(5, Math.round(el.clientWidth / 42))
      const text = { color: ink, fontSize: size }
      const category = { type: 'category' as const, data: chart.categories, axisLabel: { ...text, hideOverlap: true }, axisLine: { lineStyle: { color: ink, opacity: 0.25 } }, inverse: across }
      const value = { type: 'value' as const, axisLabel: { ...text, formatter: (v: number) => formatValue(v) }, splitLine: { lineStyle: { color: ink, opacity: 0.12 } } }
      const legend = chart.series.length > 1 || round
        ? { bottom: round ? undefined : 0, right: round ? 0 : undefined, orient: round ? ('vertical' as const) : ('horizontal' as const), top: round ? 'middle' : undefined, textStyle: text, itemWidth: size * 1.4, itemHeight: size * 0.8 }
        : undefined
      plot.setOption({
        animation: false,
        color: palette.split(','),
        textStyle: { fontFamily: 'inherit' },
        tooltip: { trigger: round ? 'item' : 'axis', valueFormatter: (v: number) => formatValue(v) },
        legend,
        grid: round ? undefined : { left: size / 2, right: size, top: size, bottom: legend ? size * 2.6 : size / 2, containLabel: true },
        xAxis: round ? undefined : chart.type === 'scatter' ? { ...value, type: 'value' } : across ? value : category,
        yAxis: round ? undefined : across ? category : value,
        series: round
          ? [{
              type: 'pie', radius: chart.type === 'doughnut' ? ['40%', '68%'] : '68%', center: ['38%', '50%'],
              data: chart.categories.map((name, i) => ({ name, value: chart.series[0]?.values[i] ?? null })),
              label: { formatter: '{d}%', color: ink, fontSize: size }, labelLine: { length: size, length2: size / 2 }, itemStyle: { borderColor: '#fff', borderWidth: 1 },
            }]
          : chart.series.map((s) => ({
              name: s.name,
              type: chart.type === 'scatter' ? 'scatter' : chart.type === 'line' || chart.type === 'area' ? 'line' : 'bar',
              data: chart.type === 'scatter' ? chart.categories.map((c, i) => [Number(c.replace(/,/g, '')), s.values[i] ?? null]) : s.values,
              stack: chart.type === 'stacked' ? 'all' : undefined,
              areaStyle: chart.type === 'area' ? { opacity: 0.7 } : undefined,
              showSymbol: chart.type === 'scatter',
              symbolSize: size * 0.7,
              lineStyle: { width: Math.max(1, size / 5) },
              barMaxWidth: size * 4,
            })),
      }, true)
      plot.resize()
    }
    draw()
    const resize = new ResizeObserver(draw)
    resize.observe(el)
    return () => {
      resize.disconnect()
      plot.dispose()
    }
  }, [chart, palette, ink])
  // oxlint-disable-next-line jsx-a11y/prefer-tag-over-role -- ECharts draws into a div; the slide's form holds the numbers
  return <div ref={ref} role="img" aria-label={label} className="h-full w-full" />
}
