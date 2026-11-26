import { useSearchParams } from 'react-router'

/**
 * A page's filters, kept in its address beside its time window (from and to): a link opens the same view, Back goes to the
 * filters before, and a reload keeps them. Each filter is text; an empty one is not in the address.
 */
export function useFilters<K extends string>(keys: readonly K[], fallback: Partial<Record<K, string>> = {}) {
  const [params, setParams] = useSearchParams()
  const values = Object.fromEntries(keys.map((k) => [k, params.get(k) ?? fallback[k] ?? ''])) as Record<K, string>
  const set = (patch: Partial<Record<K, string>>) =>
    setParams(
      (p) => {
        const next = new URLSearchParams(p)
        for (const [k, v] of Object.entries(patch) as [K, string | undefined][]) {
          if (v && v !== fallback[k]) next.set(k, v)
          else next.delete(k)
        }
        return next
      },
      { replace: true },
    )
  /** How many differ from their fallback: for "Clear the filters". */
  const active = keys.filter((k) => values[k] !== (fallback[k] ?? '')).length
  const clear = () => set(Object.fromEntries(keys.map((k) => [k, ''])) as Partial<Record<K, string>>)
  return { values, set, active, clear }
}
