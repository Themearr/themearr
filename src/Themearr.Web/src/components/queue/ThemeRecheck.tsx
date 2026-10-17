import { useCallback, useEffect, useRef, useState } from 'react'
import { recheckApi } from '@/lib/api'
import { boundedRequest } from '@/lib/boundedRequest'
import type { RecheckItem } from '@/lib/types'
import { Button, Spinner } from '@/components/ui'

/** Independent of pending triage: flagged items already have files on disk. */
export function ThemeRecheck() {
  const [items, setItems] = useState<RecheckItem[] | null>(null)
  const [error, setError] = useState('')
  const [scanning, setScanning] = useState(false)
  const [summary, setSummary] = useState('')
  const [acting, setActing] = useState<Record<string, boolean>>({})
  const mounted = useRef(false)
  const sequence = useRef(0)
  const starting = useRef(false)
  const mutations = useRef(new Set<string>())

  const refresh = useCallback(async () => {
    const mine = ++sequence.current
    try {
      const [flagged, status] = await Promise.all([
        boundedRequest(signal => recheckApi.flagged({ signal })),
        boundedRequest(signal => recheckApi.status({ signal })),
      ])
      if (!mounted.current || mine !== sequence.current) return
      setItems(flagged)
      setError(status.error || '')
      if (!starting.current) setScanning(status.inProgress)
      if (status.finished && !starting.current) {
        setSummary(`${status.scanned} themes checked; ${status.flagged} need attention.`)
      }
    } catch (e) {
      if (mounted.current && mine === sequence.current) {
        setError((e as Error).message)
        setScanning(false)
      }
    }
  }, [])

  useEffect(() => {
    mounted.current = true
    let inFlight = false
    const poll = async () => {
      if (inFlight || starting.current || mutations.current.size > 0) return
      inFlight = true
      try { await refresh() } finally { inFlight = false }
    }
    void poll()
    const timer = setInterval(() => { void poll() }, 2000)
    return () => { mounted.current = false; clearInterval(timer) }
  }, [refresh])

  async function scan() {
    if (starting.current || scanning) return
    starting.current = true
    sequence.current++
    setScanning(true)
    setSummary('')
    setError('')
    try {
      await boundedRequest(() => recheckApi.start())
      if (!mounted.current) return
      starting.current = false
      await refresh()
    } catch (e) {
      if (!mounted.current) return
      setError((e as Error).message)
      setScanning(false)
    } finally { starting.current = false }
  }

  async function resolve(item: RecheckItem, action: 'keep' | 'replace') {
    const key = `${item.mediaType}:${item.id}`
    if (mutations.current.has(key)) return
    mutations.current.add(key)
    sequence.current++
    setActing(prev => ({ ...prev, [key]: true }))
    setError('')
    try {
      await boundedRequest(() => recheckApi[action](item.id, item.mediaType))
      if (!mounted.current) return
      // Reflect only this successful decision; a failed refresh keeps the other rows.
      setItems(prev => prev?.flatMap(row => row.id === item.id && row.mediaType === item.mediaType
        ? action === 'keep' ? [] : [{ ...row, state: 'queued' as const }]
        : [row]) ?? null)
      await refresh()
    } catch (e) {
      if (mounted.current) setError((e as Error).message)
    } finally {
      mutations.current.delete(key)
      if (mounted.current) setActing(prev => ({ ...prev, [key]: false }))
    }
  }

  return (
    <section id="needs-attention" aria-label="Needs attention" className="my-5 max-w-2xl rounded-xl border border-[#1D2939] bg-[#101828] p-4 space-y-4 scroll-mt-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold text-[#F9FAFB]">Needs attention{items ? ` (${items.length})` : ''}</h2>
          <p className="mt-1 text-xs text-[#98A2B3]">Review existing themes. Your current audio stays until a replacement is ready.</p>
        </div>
        <Button variant="secondary" size="sm" onClick={() => { void scan() }} disabled={scanning}>
          {scanning && <Spinner size={14} />}{scanning ? 'Checking themes…' : 'Re-check themes'}
        </Button>
      </div>
      {summary && <p role="status" className="text-xs text-[#98A2B3]">{summary}</p>}
      {error && <div role="alert" className="flex items-center justify-between gap-3 text-sm text-[#FDA29B]">
        <p>{error}</p><Button variant="ghost" size="sm" onClick={() => { void refresh() }}>Retry re-check</Button>
      </div>}
      {items === null && !error && <p className="text-xs text-[#98A2B3]">Loading themes for review…</p>}
      {items?.length === 0 && <p className="text-xs text-[#98A2B3]">No themes need attention.</p>}
      {items?.map(item => {
        const key = `${item.mediaType}:${item.id}`
        return <article key={key} className="border-t border-[#1D2939] pt-3 space-y-2">
          <div className="flex items-center gap-2">
            <h3 className="text-sm font-medium text-[#F9FAFB]">{item.title}</h3>
            <span className="text-xs text-[#98A2B3]">{item.mediaType === 'show' ? 'Show' : 'Movie'}{item.year ? ` · ${item.year}` : ''}</span>
          </div>
          {item.themeTitle && <p className="text-sm text-[#D0D5DD]">{item.themeTitle}</p>}
          <p className="text-xs text-[#F79009]">{item.reason}</p>
          {item.state === 'queued' && <p className="text-xs text-[#98A2B3]">Queued for replacement when auto-download is enabled.</p>}
          <div className="flex gap-2">
            <Button variant="secondary" size="sm" disabled={acting[key] || item.state === 'queued'} onClick={() => { void resolve(item, 'replace') }}>Find replacement</Button>
            <Button variant="ghost" size="sm" disabled={acting[key]} onClick={() => { void resolve(item, 'keep') }}>Keep this</Button>
          </div>
        </article>
      })}
    </section>
  )
}
