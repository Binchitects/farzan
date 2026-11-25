import '@testing-library/jest-dom/vitest'
import { cleanup, configure } from '@testing-library/react'
import { afterEach, beforeAll, expect, vi } from 'vitest'

// findBy* and waitFor wait up to 5 s, not 1: a page's first render (its code
// loaded on demand) can take longer than a second on a busy CI runner.
configure({ asyncUtilTimeout: 5000 })

// The pages a test file opens (its renderApp('...') paths; a template's part before its first ${...}) have their code
// loaded before its first test, so no wait in a test pays for importing a page. Waits for the imports to finish, however
// long a loaded machine takes.
beforeAll(async () => {
  const file = expect.getState().testPath
  if (!file) return
  const { readFileSync } = await import('node:fs')
  const text = readFileSync(file, 'utf8')
  const paths = [...text.matchAll(/renderApp\(\s*(['"`])((?:(?!\1).)*?)(?:\1|\$\{)/g)].map((m) => m[2]).filter((p) => p.startsWith('/'))
  if (paths.length === 0) return
  const { preloadRoutes } = await import('./utils')
  await preloadRoutes(paths)
}, 120_000)

afterEach(() => {
  cleanup()
  try {
    localStorage.clear()
  } catch {
    // no storage in this environment
  }
  document.documentElement.className = ''
  delete document.documentElement.dataset.width
  delete document.documentElement.dataset.colorTheme
})

// What jsdom lacks and Radix, cmdk and the theme use.
if (!window.matchMedia) {
  window.matchMedia = (query: string) =>
    ({ matches: false, media: query, onchange: null, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {}, dispatchEvent: () => false }) as MediaQueryList
}
globalThis.ResizeObserver ??= class {
  observe() {}
  unobserve() {}
  disconnect() {}
} as unknown as typeof ResizeObserver
Element.prototype.hasPointerCapture ??= () => false
Element.prototype.setPointerCapture ??= () => {}
Element.prototype.releasePointerCapture ??= () => {}
// Chrome returns a Promise from scrollIntoView; a bare `() => el.scrollIntoView()`
// effect would return it to React and crash on cleanup. Behave like Chrome.
Element.prototype.scrollIntoView = function () {
  return Promise.resolve() as unknown as void
}
vi.stubGlobal('scrollTo', () => {})
Element.prototype.scrollTo ??= function () {} as Element['scrollTo']
