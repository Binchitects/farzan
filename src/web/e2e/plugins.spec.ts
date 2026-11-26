import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

/**
 * Plugins' pages in a real browser, from the framework's examples (tests/fixtures/plugins): the hello plugin's page in
 * the menu, its frame talking to Arena only through the bridge; and the chaos plugin's pages, each broken its own way
 * (throws as it loads, never loads, floods the bridge, tries what a frame must not), each shown as not working while
 * the page around it goes on. Runs against an Arena with the plugin runtime (tools/plugin-dev-stack.sh up).
 */
const fixtures = fileURLToPath(new URL('../../../tests/fixtures/plugins/', import.meta.url))
const headers = { 'X-Requested-With': 'e2e' }

async function install(request: APIRequestContext, name: string) {
  await request.post('/api/admin/plugins/publishers', {
    headers, data: { name: 'Framework examples', publicKeyPem: readFileSync(fixtures + 'keys/publisher.pub.pem', 'utf8') },
  })
  const zip = readFileSync(`${fixtures}packages/${name}.fap`).toString('base64')
  const res = await request.post('/api/admin/plugins/install', { headers, data: { zip, accept: true } })
  expect([201, 409]).toContain(res.status())
}

const frame = (page: Page, plugin: string) => page.locator(`iframe[data-plugin="${plugin}"]`)

test.describe.configure({ mode: 'serial' })

test.beforeAll(async ({ request }) => {
  for (const name of ['hello', 'chaos']) await install(request, name)
})

test('a plugin page is in the menu, sandboxed, and reaches Arena only through the bridge', async ({ page }) => {
  await page.goto('/')
  await page.getByRole('link', { name: 'Hello' }).first().click()
  await expect(page).toHaveURL(/\/p\/hello\/home$/)
  const f = frame(page, 'hello')
  await expect(f).toHaveAttribute('sandbox', 'allow-scripts allow-forms allow-popups allow-modals')
  const inside = f.contentFrame()
  await expect(inside.locator('#who')).toContainText('Signed in as')
  await inside.getByRole('button', { name: 'Say hello' }).click()
  await expect(inside.locator('#answer')).toContainText(/^Hello, .+! \(\d+\)$/)
  // The page's own toast, asked for by the frame.
  await expect(page.getByText(/Hello, .+! \(\d+\)/).first()).toBeVisible()
})

test('a page that throws as it loads shows its card, and the rest of Arena works', async ({ page }) => {
  await page.goto('/p/chaos/throws')
  await expect(page.locator('[data-plugin-error]')).toContainText('Chaos is not working', { timeout: 15_000 })
  await expect(page.locator('[data-plugin-error]')).toContainText('did not start')
  await page.getByRole('link', { name: 'Hello' }).first().click()
  await expect(frame(page, 'hello').contentFrame().locator('#who')).toContainText('Signed in as')
})

test('a page that never loads (an endless loop) is given up on, and the page around it stays quick', async ({ page }) => {
  await page.goto('/p/chaos/frozen')
  // The page itself answers at once while the frame spins in its own process.
  const t = Date.now()
  await page.getByRole('button', { name: /open navigation|collapse|expand/i }).first().isVisible()
  expect(Date.now() - t).toBeLessThan(2000)
  await expect(page.locator('[data-plugin-error]')).toContainText('did not start', { timeout: 15_000 })
  await page.getByRole('button', { name: 'Try again' }).click()
  await expect(frame(page, 'chaos')).toBeVisible()
})

test('a frame that floods the bridge is limited, then stopped, and the page goes on', async ({ page }) => {
  await page.goto('/p/chaos/flood')
  await expect(page.locator('[data-plugin-error]')).toContainText('far more calls than it may', { timeout: 15_000 })
  await page.goto('/p/hello/home')
  await expect(frame(page, 'hello').contentFrame().locator('#who')).toContainText('Signed in as')
})

test('a frame cannot read cookies or storage, reach the API or the web, move the top window, or open Arena outside its own', async ({ page }) => {
  await page.goto('/p/chaos/escape')
  const out = frame(page, 'chaos').contentFrame().locator('#out')
  await expect(out).toContainText('another plugin', { timeout: 15_000 })
  const text = (await out.textContent()) ?? ''
  expect(text.split('\n').filter((l) => l.includes('REACHED'))).toEqual([])
  for (const what of ['cookies', 'local storage', "the parent's document", 'the API by fetch', 'another site by fetch', 'the top window', 'navigating Arena outside its own pages']) {
    expect(text).toContain(`${what}: refused`)
  }
  await expect(page).toHaveURL(/\/p\/chaos\/escape$/)
})
