import { useCallback, useEffect, useRef, useState } from 'react'

/**
 * Loads a resource with three outcomes rather than two.
 *
 * The bug this exists to prevent: pages used `null` or `[]` to mean both "nothing
 * here" and "we never found out", so a failed request rendered as a reassuring
 * empty state — "No movies yet", "All caught up!" — and an outage was
 * indistinguishable from an empty library. Keeping `error` separate from `data`
 * makes that confusion unrepresentable.
 */
export function useResource<T>(fetcher: () => Promise<T>) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [attempt, setAttempt] = useState(0)

  // Identifies the newest request, so a slow earlier one cannot overwrite it.
  const latest = useRef(0)
  const fetcherRef = useRef(fetcher)
  // Refs are only ever written outside of render (React disallows mutating
  // them during render), so keep this synced via an effect that runs after
  // every render rather than assigning it inline above.
  useEffect(() => {
    fetcherRef.current = fetcher
  })

  useEffect(() => {
    const mine = ++latest.current
    fetcherRef.current()
      .then(value => {
        if (mine !== latest.current) return
        setData(value)
        setError(null)
      })
      .catch((e: unknown) => {
        if (mine !== latest.current) return
        setError(e instanceof Error && e.message ? e.message : 'Request failed')
      })
      .finally(() => {
        if (mine === latest.current) setLoading(false)
      })
  }, [attempt])

  const retry = useCallback(() => {
    setError(null)
    setLoading(true)
    setAttempt(a => a + 1)
  }, [])

  return { data, error, loading, retry }
}
