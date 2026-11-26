import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { visibleNavigation } from '@/app/nav'
import { admin, fakeApi, member, renderApp } from '@/test/utils'

const row = (n: number, source: 'chat' | 'api') => ({
  source, at: `2026-10-28T10:${String(59 - n).padStart(2, '0')}:00Z`,
  person: { id: 'p1', userName: 'ann', displayName: 'Ann' }, model: 'Qwen3-4B',
  question: `question ${n}`, answer: source === 'chat' ? `answer ${n}` : null,
  chat: source === 'chat' ? 'c1' : null, title: source === 'chat' ? 'Trip plans' : null, deleted: n === 1, tokens: source === 'api' ? 120 : null,
  state: 'done', error: null, durationMs: 1500,
})

describe('prompt log', () => {
  it('keeps its search in the address: a link opens the same one, by model, tool and run, and Clear goes back to all', async () => {
    const calls = fakeApi(admin, { 'GET /api/admin/prompts': () => ({ json: { items: [], next: null } }) })
    const { router } = renderApp('/admin/prompts?model=Qwen3-4B&tool=read_file&run=r1&source=api')
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/prompts?source=api&model=Qwen3-4B&tool=read_file&run=r1')).toBe(true))
    expect(screen.getByLabelText('Model')).toHaveValue('Qwen3-4B')
    expect(screen.getByText('Only the steps of one run.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Clear the filters' }))
    await waitFor(() => expect(router.state.location.search).toBe(''))
    await waitFor(() => expect(calls.at(-1)?.path).toBe('/api/admin/prompts?source=all'))
  })

  it('opens a step whole, its tool call with its arguments and the result it got, and its run by thread', async () => {
    const step = (id: string, more: object) => ({
      source: 'api', id, at: '2026-11-26T10:00:00Z', model: 'Qwen3-4B', state: 'done', error: null, durationMs: 900, promptTokens: 2000, completionTokens: 40, chat: 'r1',
      kept: true, from: 2, total: 4, tools: ['read_file'], settings: { temperature: 0.2 },
      messages: [
        { role: 'assistant', content: '', toolCalls: [{ id: 'call_1', name: 'read_file', arguments: '{"path":"src/a.cs"}' }] },
        { role: 'tool', content: 'class A { int x = ; }', toolCallId: 'call_1' },
      ],
      answer: { finish: 'stop', reasoning: 'The semicolon is misplaced.', content: 'Fixed: `int x = 0;`', toolCalls: null },
      ...more,
    })
    fakeApi(admin, {
      'GET /api/admin/prompts': () => ({ json: { items: [{ ...row(0, 'api'), id: 's2', run: 'r1' }], next: null } }),
      'GET /api/admin/prompts/api/s2/detail': () => ({ json: step('s2', {}) }),
      'GET /api/admin/prompts/api/s3/detail': () => ({ json: step('s3', { from: 0, total: 2, messages: [{ role: 'user', content: 'Find where A is used.' }], answer: { finish: 'stop', reasoning: null, content: 'In B.', toolCalls: null } }) }),
      'GET /api/admin/prompts/runs/r1': () => ({
        json: {
          chat: 'r1', title: 'Fix the build', kind: 'session', threads: [{ id: 't1', name: 'Main' }, { id: 't2', name: 'Sub-agent 1' }], questions: [],
          steps: [
            { id: 's1', at: '2026-11-26T09:59:00Z', thread: 't1', model: 'Qwen3-4B', state: 'done', error: null, durationMs: 800, tokens: 1900, said: 'Called read_file' },
            { id: 's2', at: '2026-11-26T10:00:00Z', thread: 't1', model: 'Qwen3-4B', state: 'done', error: null, durationMs: 900, tokens: 2040, said: 'Fixed' },
            { id: 's3', at: '2026-11-26T10:00:30Z', thread: 't2', model: 'Qwen3-4B', state: 'done', error: null, durationMs: 400, tokens: 300, said: 'In B.' },
          ],
        },
      }),
    })
    renderApp('/admin/prompts')
    await userEvent.click(await screen.findByRole('button', { name: /The whole run/ }))
    const dialog = await screen.findByRole('dialog', { name: 'The request, whole' })
    const sent = await within(dialog).findByRole('region', { name: 'What it sent' })
    expect(within(sent).getByText(/messages 3 to 4/)).toBeInTheDocument()
    expect(within(sent).getByLabelText('Arguments of read_file')).toHaveTextContent('"path": "src/a.cs"')
    // The tool's result is folded until opened; the thinking too.
    await userEvent.click(within(sent).getByRole('button', { name: /Result of a tool/ }))
    expect(within(sent).getByText('class A { int x = ; }')).toBeInTheDocument()
    const got = within(dialog).getByRole('region', { name: 'What it got' })
    await userEvent.click(within(got).getByRole('button', { name: /Thinking/ }))
    expect(within(got).getByText('The semicolon is misplaced.')).toBeInTheDocument()
    // Its run: each thread's steps; the sub-agent's step opens whole.
    await userEvent.click(within(dialog).getByRole('tab', { name: 'Its run' }))
    const sub = await within(dialog).findByRole('region', { name: 'Sub-agent 1' })
    await userEvent.click(within(sub).getByRole('button', { name: /In B\./ }))
    expect(await within(dialog).findByText('Find where A is used.')).toBeInTheDocument()
  })

  it("shows a question's pictures and the files its answer made, each read from the log's own address", async () => {
    const file = (id: string, name: string, kind: string, contentType: string) => ({ id, name, kind, contentType, size: 2048, url: `/api/admin/prompts/files/chat/${id}` })
    fakeApi(admin, {
      'GET /api/admin/prompts': () => ({
        json: {
          items: [{ ...row(0, 'chat'), files: [file('f1', 'diagram.png', 'image', 'image/png')], made: [file('f2', 'report.csv', 'text', 'text/csv'), file('f3', 'chart.png', 'image', 'image/png')] }],
          next: null,
        },
      }),
    })
    renderApp('/admin/prompts')
    const asked = await screen.findByRole('region', { name: 'With the question' })
    expect(within(asked).getByRole('img', { name: 'diagram.png' })).toHaveAttribute('src', '/api/admin/prompts/files/chat/f1')
    const made = screen.getByRole('region', { name: 'Made by the answer' })
    expect(within(made).getByRole('img', { name: 'chart.png' })).toHaveAttribute('src', '/api/admin/prompts/files/chat/f3')
    expect(within(made).getByRole('link', { name: /report\.csv/ })).toHaveAttribute('href', '/api/admin/prompts/files/chat/f2')
  })

  it('lists questions and answers, searches by words, person and where, and pages to older ones', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/prompts': (_, __, url) => {
        if (url.searchParams.get('before')) return { json: { items: [row(9, 'api')], next: null } }
        return { json: { items: [row(0, 'chat'), row(1, 'chat'), row(2, 'api')], next: '2026-10-28T10:57:00Z' } }
      },
    })
    renderApp('/admin/prompts')
    const list = await screen.findByRole('list', { name: 'Prompt log' })
    const items = await within(list).findAllByRole('listitem')
    expect(items).toHaveLength(3)
    expect(within(items[0]).getByText('question 0')).toBeInTheDocument()
    expect(within(items[0]).getByText('answer 0')).toBeInTheDocument()
    expect(within(items[0]).getByText('in “Trip plans”')).toBeInTheDocument()
    expect(within(items[0]).getByRole('link', { name: 'Ann' })).toHaveAttribute('href', '/admin/people/p1')
    expect(within(items[1]).getByText('chat deleted')).toBeInTheDocument()
    expect(within(items[2]).getByText('API')).toBeInTheDocument()
    expect(within(items[2]).getByText(/120 tokens/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Older' }))
    await waitFor(() => expect(within(list).getAllByRole('listitem')).toHaveLength(4))
    expect(calls.some((c) => c.path.includes('before=2026-10-28T10%3A57%3A00Z'))).toBe(true)

    await userEvent.type(screen.getByLabelText('Words'), 'budget')
    await userEvent.type(screen.getByLabelText('Person'), 'ann')
    await userEvent.click(screen.getByRole('button', { name: 'Search' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/prompts?source=all&q=budget&person=ann')).toBe(true))
    await userEvent.click(screen.getByRole('radio', { name: 'API' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/prompts?source=api&q=budget&person=ann')).toBe(true))
  })

  it('shows each state with why it failed, an answer being written, and the whole of a long answer on Show all', async () => {
    const long = Array.from({ length: 30 }, (_, k) => `line ${k}`).join('\n')
    const calls = fakeApi(admin, {
      'GET /api/admin/prompts': () => ({
        json: {
          items: [
            { ...row(0, 'api'), state: 'running', answer: null, durationMs: null },
            { ...row(1, 'api'), state: 'failed', error: 'Budget has been exceeded!' },
            { ...row(2, 'api'), state: 'refused', error: 'The line is full: try again in a moment.' },
            { ...row(3, 'api'), state: 'stopped', answer: 'Once upon', error: 'The caller went away before the answer was whole.' },
            { ...row(4, 'chat'), answer: long },
          ],
          next: null,
        },
      }),
    })
    renderApp('/admin/prompts')
    const items = await within(await screen.findByRole('list', { name: 'Prompt log' })).findAllByRole('listitem')
    expect(within(items[0]).getByText('Running')).toBeInTheDocument()
    expect(within(items[0]).getByText('Being answered…')).toBeInTheDocument()
    expect(within(items[1]).getByText('Failed')).toBeInTheDocument()
    expect(within(items[1]).getByRole('note')).toHaveTextContent('Budget has been exceeded!')
    expect(within(items[2]).getByText('Refused')).toBeInTheDocument()
    expect(within(items[3]).getByRole('region', { name: 'Answer, as far as it went' })).toHaveTextContent('Once upon')
    expect(within(items[4]).getByText('took 1.50 s', { exact: false })).toBeInTheDocument()

    const answer = within(items[4]).getByRole('region', { name: 'Answer' })
    const more = within(answer).getByRole('button', { name: /Show all/ })
    expect(more).toHaveAttribute('aria-expanded', 'false')
    await userEvent.click(more)
    expect(within(answer).getByRole('button', { name: 'Show less' })).toHaveAttribute('aria-expanded', 'true')

    await userEvent.click(screen.getByRole('combobox', { name: 'State' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Failed' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/prompts?source=all&state=failed')).toBe(true))
  })

  it('writes a report on the filtered lines with the model chosen, and keeps it as Markdown', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/prompts': () => ({ json: { items: [row(0, 'api')], next: null } }),
      'GET /api/chat/config': () => ({ json: { model: 'Qwen3-4B', models: [{ name: 'Qwen3-4B', loaded: true }, { name: 'Big-70B', loaded: true }] } }),
      'POST /api/admin/prompts/write': () => ({
        events: [
          { type: 'progress', done: 0, of: 3, lines: 40 },
          { type: 'progress', done: 2, of: 3, lines: 40 },
          { type: 'report', markdown: '# Ann asks well\n\nMostly clear.', lines: 40, parts: 2, window: '2026-11-01 to 2026-11-26', filters: 'person ann' },
        ],
      }),
    })
    renderApp('/admin/prompts?person=ann&source=api')
    await userEvent.click(await screen.findByRole('button', { name: 'Write a report' }))
    const dialog = await screen.findByRole('dialog', { name: 'Write a report on these lines' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Failures' }))
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Model' }))
    await userEvent.click(await screen.findByRole('option', { name: 'Big-70B' }))
    await userEvent.click(within(dialog).getByRole('button', { name: /Write it/ }))
    const report = await within(dialog).findByRole('region', { name: 'The report' })
    expect(within(report).getByRole('heading', { name: 'Ann asks well' })).toBeInTheDocument()
    expect(within(report).getByText(/40 lines, 2026-11-01 to 2026-11-26; person ann/)).toBeInTheDocument()
    const sent = calls.find((c) => c.path === '/api/admin/prompts/write')!.body as Record<string, unknown>
    expect(sent).toMatchObject({ person: 'ann', source: 'api', model: 'Big-70B', state: null })
    expect(sent.ask).toMatch(/What went wrong/)
  })

  it('is in the menu only for those who may review prompts', () => {
    const titles = (me: Parameters<typeof visibleNavigation>[0]) => visibleNavigation(me).flatMap((s) => s.items.map((i) => i.title))
    expect(titles(admin)).toContain('Prompt log')
    expect(titles({ ...member, permissions: ['audit.read', 'usage.read'] })).not.toContain('Prompt log')
    expect(titles({ ...member, permissions: ['prompts.review'] })).toContain('Prompt log')
  })

  it('tags a question, summarizes it, searches by tag and reports by model, person and tag', async () => {
    let tags: string[] = []
    let brief: string | null = null
    const calls = fakeApi(admin, {
      'GET /api/tags': () => ({ json: { known: ['review', 'security'], used: [{ tag: 'later', count: 2 }] } }),
      'GET /api/admin/prompts': () => ({ json: { items: [{ ...row(0, 'chat'), id: 'm1', tags, brief }], next: null } }),
      'PUT /api/admin/prompts/chat/m1/tags': (body) => {
        tags = (body as { tags: string[] }).tags
        return { json: { tags } }
      },
      'POST /api/admin/prompts/chat/m1/summarize': () => {
        brief = 'Asks about the trip.'
        tags = [...tags, 'security']
        return { json: { brief, tags } }
      },
      'GET /api/admin/prompts/report': () => ({
        json: {
          api: 4, apiFailed: 1, apiRefused: 0, chat: 3, chatFailed: 0, apiP95Ms: 2500, chatP95Ms: 4000,
          apiModels: [{ name: 'Qwen3-4B', count: 4, failed: 1, stopped: 0, refused: 0, averageMs: 1000, medianMs: 900, p95Ms: 2500, tokens: 480, api: 0, chat: 0 }],
          chatModels: [], people: [{ name: 'Ann', count: 7, failed: 0, stopped: 0, refused: 0, averageMs: null, medianMs: null, p95Ms: null, tokens: 0, api: 4, chat: 3 }],
          tags: [{ name: 'security', count: 2, failed: 0, stopped: 0, refused: 0, averageMs: null, medianMs: null, p95Ms: null, tokens: 0, api: 0, chat: 2 }], days: [],
        },
      }),
    })
    renderApp('/admin/prompts')
    await userEvent.click(await screen.findByRole('button', { name: 'Tags and summary' }))
    const dialog = await screen.findByRole('dialog', { name: /Tags and summary/ })
    await userEvent.click(within(dialog).getByRole('button', { name: '+ review' }))
    await userEvent.type(within(dialog).getByRole('textbox', { name: 'Tags' }), 'For Later{Enter}')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save tags' }))
    await waitFor(() => expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ tags: ['review', 'for-later'] }))
    expect(await screen.findByText('#for-later')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Tags and summary' }))
    const again = await screen.findByRole('dialog', { name: /Tags and summary/ })
    await userEvent.click(within(again).getByRole('button', { name: /Summarize and tag/ }))
    expect(await within(again).findByLabelText('Summary')).toHaveTextContent('Asks about the trip.')
    expect(within(again).getByText('#security')).toBeInTheDocument()
    await userEvent.keyboard('{Escape}')

    await userEvent.click(screen.getByRole('combobox', { name: 'Tag' }))
    await userEvent.click(await screen.findByRole('option', { name: '#later' }))
    await waitFor(() => expect(calls.some((c) => c.path.startsWith('/api/admin/prompts?') && c.path.includes('tag=later'))).toBe(true))

    await userEvent.click(screen.getByRole('tab', { name: 'Report' }))
    const byModel = await screen.findByRole('region', { name: 'API requests by model' })
    expect(within(byModel).getByRole('row', { name: /Qwen3-4B/ })).toHaveTextContent('25.0%')
    expect(within(screen.getByRole('region', { name: 'By tag' })).getByRole('row', { name: /security/ })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /CSV/ })).toHaveAttribute('href', expect.stringContaining('format=csv'))
  })
})
