import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { admin, fakeApi, renderApp } from '@/test/utils'

const event = { id: 7, at: '2026-11-26T10:00:00Z', actor: 'admin', action: 'prompts.viewed', target: null, success: true, ip: '10.0.0.1', detail: 'source all' }

describe('audit log', () => {
  it('searches every event by words and action on the server, keeps the search in the address, and shows only failures', async () => {
    const calls = fakeApi(admin, { 'GET /api/admin/audit': () => ({ json: [event] }) })
    const { router } = renderApp('/admin/audit')
    expect(await screen.findByText('prompts.viewed')).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Words'), 'ann')
    await userEvent.type(screen.getByLabelText('Action'), 'prompts.')
    await userEvent.click(screen.getByRole('button', { name: 'Search' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/audit?take=500&q=ann&action=prompts.')).toBe(true))
    expect(router.state.location.search).toBe('?q=ann&action=prompts.')
    await userEvent.click(screen.getByRole('radio', { name: 'Failed' }))
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/audit?take=500&q=ann&action=prompts.&success=false')).toBe(true))
  })
})
