// SPDX-License-Identifier: AGPL-3.0-only OR LicenseRef-Farzan-Commercial
// Copyright (C) 2026 Binchitects
import { describe, expect, it, vi } from 'vitest'
import { allowedPath, BURST, createBridge, MOST_REFUSED, type BridgeHost, type FrameContext } from './plugin-bridge'

const context: FrameContext = {
  plugin: { name: 'desk', version: '1.0.0', title: 'Desk' }, slot: 'page', id: 'board', rest: '', person: { id: 'p1', name: 'Ann' },
  chatId: null, projectId: null, message: null, review: null, theme: { mode: 'light', tokens: {} }, locale: 'en', dev: false,
}

function setup(over: Partial<BridgeHost> = {}, slot: FrameContext['slot'] = 'page', writes: string[] = []) {
  const replies: Record<string, unknown>[] = []
  let t = 0
  const host: BridgeHost = {
    call: vi.fn(async () => ({ result: 'answered' })) as never,
    navigate: vi.fn(), toast: vi.fn(), resize: vi.fn(), close: vi.fn(), insert: vi.fn(), confirm: vi.fn(async () => true), broke: vi.fn(), hello: vi.fn(),
    now: () => t, ...over,
  }
  const bridge = createBridge({ plugin: 'desk', context: { ...context, slot }, writes, host }, (m) => replies.push(m as Record<string, unknown>))
  return { bridge, host, replies, tick: (ms: number) => { t += ms } }
}

const msg = (id: number, method: string, params: Record<string, unknown> = {}) => ({ arena: 1, id, method, params })

describe('the bridge between a plugin frame and the page', () => {
  it('answers hello with the context, and nothing before it', async () => {
    const { bridge, replies } = setup()
    await bridge.receive(msg(1, 'version'))
    expect(replies[0]).toMatchObject({ id: 1, error: { code: -32002 } })
    await bridge.receive(msg(2, 'hello'))
    expect(replies[1]).toMatchObject({ id: 2, result: { plugin: { name: 'desk' }, slot: 'page' } })
    expect(bridge.greeted).toBe(true)
  })

  it('ignores what is not a bridge message', async () => {
    const { bridge, replies } = setup()
    for (const junk of [null, 'hello', 42, { arena: 2, id: 1, method: 'hello' }, { arena: 1, method: 'hello' }, { arena: 1, id: {}, method: 'hello' }]) await bridge.receive(junk)
    expect(replies).toEqual([])
  })

  it('relays host API calls to Arena, marked as the bridge\'s, and passes Arena\'s refusal on', async () => {
    const call = vi.fn(async () => ({ error: { code: -32001, message: 'The plugin was not granted people.read.' } }))
    const { bridge, replies } = setup({ call: call as never })
    await bridge.receive(msg(1, 'hello'))
    await bridge.receive(msg(2, 'people.search', { query: 'a' }))
    expect(call).toHaveBeenCalledWith('/api/plugins/desk/host', expect.objectContaining({ headers: { 'X-Arena-Bridge': '1' }, body: expect.objectContaining({ method: 'people.search' }) }))
    expect(replies[1]).toEqual({ arena: 1, id: 2, error: { code: -32001, message: 'The plugin was not granted people.read.' } })
  })

  it('opens only the plugin\'s own pages, a chat or a project', async () => {
    expect(allowedPath('desk', '/p/desk/board')).toBe(true)
    expect(allowedPath('desk', '/chat/0193a4b2-7c1d-7e3f-9a8b-1234567890ab')).toBe(true)
    for (const bad of ['/admin/people', '/p/other/page', '//evil.example.com', 'https://evil.example.com', '/p/desk/../../admin', '/p/desk\\..\\admin', '/p/desk/\u0000']) {
      expect(allowedPath('desk', bad), bad).toBe(false)
    }
    const { bridge, host, replies } = setup()
    await bridge.receive(msg(1, 'hello'))
    await bridge.receive(msg(2, 'ui.navigate', { path: '/admin/people' }))
    expect(host.navigate).not.toHaveBeenCalled()
    expect(replies[1]).toMatchObject({ error: { code: -32001 } })
  })

  it('puts text in the composer only for a composer action, and keeps a frame\'s height in bounds', async () => {
    const page = setup()
    await page.bridge.receive(msg(1, 'hello'))
    await page.bridge.receive(msg(2, 'ui.insert', { text: 'x' }))
    expect(page.host.insert).not.toHaveBeenCalled()
    await page.bridge.receive(msg(3, 'ui.resize', { height: 1e9 }))
    expect(page.host.resize).toHaveBeenCalledWith(4000)
    const action = setup({}, 'composer-action')
    await action.bridge.receive(msg(1, 'hello'))
    await action.bridge.receive(msg(2, 'ui.insert', { text: 'T-12 ' }))
    expect(action.host.insert).toHaveBeenCalledWith('T-12 ')
  })

  it('asks the person before a tool that changes something, and runs nothing when they say no', async () => {
    const call = vi.fn(async () => ({ content: 'done', isError: false }))
    const { bridge, host, replies } = setup({ call: call as never, confirm: vi.fn(async () => false) }, 'page', ['create_ticket'])
    await bridge.receive(msg(1, 'hello'))
    await bridge.receive(msg(2, 'plugin.tool', { name: 'create_ticket', arguments: { title: 'x' } }))
    expect(host.confirm).toHaveBeenCalled()
    expect(call).not.toHaveBeenCalled()
    expect(replies[1]).toMatchObject({ result: { isError: true } })
  })

  it('limits a frame that floods it, and stops one that will not stop', async () => {
    const { bridge, host, replies, tick } = setup()
    await bridge.receive(msg(0, 'hello'))
    for (let i = 1; i <= BURST + 10; i++) await bridge.receive(msg(i, 'version'))
    expect(replies.filter((r) => (r.error as { code?: number } | undefined)?.code === -32003).length).toBe(11)
    tick(1000) // a second later, fifty more
    await bridge.receive(msg(9999, 'version'))
    expect(replies.at(-1)).toMatchObject({ id: 9999, result: 'answered' })
    for (let i = 0; i < MOST_REFUSED + 60; i++) await bridge.receive(msg(20000 + i, 'version'))
    expect(host.broke).toHaveBeenCalledOnce()
    const before = replies.length
    await bridge.receive(msg(99999, 'version'))
    expect(replies.length).toBe(before)
  })
})
