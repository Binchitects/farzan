import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

describe('admin assistant', () => {
  it('shows what it read, proposes a change, does it only on yes, and tells the model only how it went', async () => {
    let asked = 0
    const calls = fakeApi(admin, {
      'GET /api/admin/people': () => ({ json: { items: [], total: 0 } }),
      'POST /api/admin-assistant/ask': () => {
        asked++
        return asked === 1
          ? {
              events: [
                { type: 'thinking', text: 'Looking at the projects first.' },
                { type: 'thought', ms: 2300 },
                { type: 'step', id: 's1', tool: 'read', path: '/api/admin/projects', arguments: '{"path":"/api/admin/projects"}', state: 'running' },
                { type: 'step', id: 's1', tool: 'read', path: '/api/admin/projects', arguments: '{"path":"/api/admin/projects"}', state: 'done', status: 200, ms: 120, result: '[{"name":"Payments"}]' },
                { type: 'delta', text: 'Payments has ' },
                { type: 'delta', text: '3 members.' },
                { type: 'round' },
                { type: 'proposal', id: 'p1', method: 'POST', path: '/api/projects/x/members', body: '{"userName":"ann"}', what: 'Adds Ann to Payments.' },
                { type: 'done' },
              ],
            }
          : { events: [{ type: 'delta', text: 'Ann is in Payments now.' }, { type: 'round' }, { type: 'done' }] }
      },
      'POST /api/admin-assistant/changes/p1/yes': () => ({ json: { status: 201, body: '{"password":"secret-one"}' } }),
    })
    renderApp('/admin/people')
    await userEvent.click(await screen.findByRole('button', { name: 'Ask the admin assistant' }))
    const panel = await screen.findByRole('dialog', { name: 'Admin assistant' })
    await userEvent.type(within(panel).getByLabelText('Your question'), 'Add Ann to Payments{Enter}')
    expect(await within(panel).findByText('Payments has 3 members.')).toBeInTheDocument()
    // As the chat shows them: the question's bubble, the thinking folded to how long it took, the step as a tool card.
    expect(within(panel).getByRole('region', { name: 'You' })).toHaveTextContent('Add Ann to Payments')
    expect(within(panel).getByText('Thought for 2.3 s')).toBeInTheDocument()
    expect(within(panel).getByRole('button', { name: /Read.*\/api\/admin\/projects/ })).toBeInTheDocument()
    const ask = calls.find((c) => c.path === '/api/admin-assistant/ask')!
    expect(ask.body).toMatchObject({ page: '/admin/people', messages: [{ role: 'user', content: 'Add Ann to Payments' }] })

    // Nothing is done before the yes.
    const card = within(panel).getByRole('region', { name: 'A proposed change' })
    expect(within(card).getByText('Adds Ann to Payments.')).toBeInTheDocument()
    expect(calls.some((c) => c.path.includes('/changes/'))).toBe(false)
    await userEvent.click(within(card).getByRole('button', { name: /Yes, do it/ }))
    expect(await within(card).findByText('Done (HTTP 201).')).toBeInTheDocument()
    expect(await within(panel).findByText('Ann is in Payments now.')).toBeInTheDocument()
    // The model hears the status, never the answer's body (a password or key is the admin's alone).
    const next = calls.filter((c) => c.path === '/api/admin-assistant/ask')[1].body as { messages: { content: string }[] }
    expect(next.messages.at(-1)!.content).toMatch(/HTTP 201/)
    expect(JSON.stringify(next)).not.toContain('secret-one')
  })

  it('floats on the page itself, so it keeps its corner when the page scrolls', async () => {
    fakeApi(admin, { 'GET /api/admin/people': () => ({ json: { items: [], total: 0 } }) })
    renderApp('/admin/people')
    const button = await screen.findByRole('button', { name: 'Ask the admin assistant' })
    expect(button.parentElement).toBe(document.body)
    expect(button).toHaveClass('fixed')
  })

  it('leaves a change undone on no, and says so', async () => {
    const calls = fakeApi(admin, {
      'GET /api/admin/people': () => ({ json: { items: [], total: 0 } }),
      'POST /api/admin-assistant/ask': () => ({ events: [{ type: 'proposal', id: 'p2', method: 'DELETE', path: '/api/admin/people/u1', body: null, what: 'Deletes Bob.' }, { type: 'done' }] }),
      'POST /api/admin-assistant/changes/p2/no': () => ({ status: 204 }),
    })
    renderApp('/admin/people')
    await userEvent.click(await screen.findByRole('button', { name: 'Ask the admin assistant' }))
    const panel = await screen.findByRole('dialog', { name: 'Admin assistant' })
    await userEvent.type(within(panel).getByLabelText('Your question'), 'Remove Bob{Enter}')
    const card = await within(panel).findByRole('region', { name: 'A proposed change' })
    await userEvent.click(within(card).getByRole('button', { name: 'No' }))
    expect(await within(card).findByText('Not done: you said no.')).toBeInTheDocument()
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin-assistant/changes/p2/no')).toBe(true))
    expect(calls.some((c) => c.path.endsWith('/yes'))).toBe(false)
  })
})
