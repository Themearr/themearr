'use client'

import { useEffect, useState } from 'react'
import { historyApi } from '@/lib/api'
import type { HistoryEntry } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { Button, Spinner } from '@/components/ui'

type DateFilter = 'all' | 'today' | 'week' | 'month'

export default function HistoryPage() {
  const [entries,    setEntries]    = useState<HistoryEntry[] | null>(null)
  const [refreshing, setRefreshing] = useState(false)
  const [search,     setSearch]     = useState('')
  const [dateFilter, setDateFilter] = useState<DateFilter>('all')

  function load() {
    historyApi.get().then(setEntries).catch(() => setEntries([]))
  }

  useEffect(() => { load() }, [])

  async function refresh() {
    setRefreshing(true)
    try { await historyApi.get().then(setEntries) } catch { /* ignore */ }
    finally { setRefreshing(false) }
  }

  const filtered = (entries ?? []).filter(e => {
    if (search.trim()) {
      const q = search.toLowerCase()
      if (!e.movieTitle.toLowerCase().includes(q) && !(e.themeTitle ?? '').toLowerCase().includes(q))
        return false
    }
    if (dateFilter !== 'all') {
      const now  = new Date()
      const date = new Date(e.downloadedAt)
      if (dateFilter === 'today') {
        if (date.toDateString() !== now.toDateString()) return false
      } else if (dateFilter === 'week') {
        if (date < new Date(now.getTime() - 7 * 24 * 60 * 60 * 1000)) return false
      } else if (dateFilter === 'month') {
        if (date < new Date(now.getTime() - 30 * 24 * 60 * 60 * 1000)) return false
      }
    }
    return true
  })

  return (
    <AppShell title="History" actions={
      <Button variant="ghost" size="sm" onClick={refresh} loading={refreshing}>
        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
          <path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8" />
          <path d="M21 3v5h-5" />
          <path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16" />
          <path d="M3 21v-5h5" />
        </svg>
        Refresh
      </Button>
    }>
      {entries === null ? (
        <div className="flex justify-center py-24">
          <Spinner size={28} className="text-[#BB0000]" />
        </div>
      ) : entries.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-24 gap-3 text-center">
          <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="#344054" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="12" cy="12" r="10" />
            <polyline points="12 6 12 12 16 14" />
          </svg>
          <p className="text-sm font-semibold text-[#D0D5DD]">No downloads yet</p>
          <p className="text-sm text-[#667085]">Themes will appear here once downloaded</p>
        </div>
      ) : (
        <div className="max-w-2xl space-y-4">
          {/* Search + filter toolbar */}
          <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <div className="relative">
              <svg className="absolute left-3 top-1/2 -translate-y-1/2 text-[#475467]" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                <circle cx="11" cy="11" r="8" /><path d="m21 21-4.35-4.35" />
              </svg>
              <input
                value={search}
                onChange={(e: { target: { value: string } }) => setSearch(e.target.value)}
                placeholder="Search history…"
                className="rounded-lg border border-[#344054] bg-[#101828] py-2 pl-9 pr-3.5 text-sm text-[#F9FAFB] placeholder:text-[#475467] outline-none focus:border-[#BB0000] focus:ring-1 focus:ring-[#BB0000]/40 w-56"
              />
            </div>
            <div className="flex items-center gap-1 rounded-lg bg-[#101828] border border-[#1D2939] p-1">
              {(['all', 'today', 'week', 'month'] as DateFilter[]).map(f => (
                <button
                  key={f}
                  onClick={() => setDateFilter(f)}
                  className={`rounded-md px-3 py-1.5 text-xs font-medium transition-all capitalize
                    ${dateFilter === f
                      ? 'bg-[#1D2939] text-[#F9FAFB] shadow-sm'
                      : 'text-[#667085] hover:text-[#D0D5DD]'}`}
                >
                  {f}
                </button>
              ))}
            </div>
          </div>

          <p className="text-sm text-[#667085]">
            {filtered.length}{filtered.length !== entries.length ? ` of ${entries.length}` : ''} theme{entries.length !== 1 ? 's' : ''}
          </p>

          {filtered.length === 0 ? (
            <p className="py-8 text-center text-sm text-[#475467]">No results match your filters.</p>
          ) : (
          <div className="rounded-xl border border-[#1D2939] overflow-hidden">
            {filtered.map((entry, i) => (
              <div
                key={entry.id}
                className={`flex items-start gap-4 px-5 py-4 ${i < filtered.length - 1 ? 'border-b border-[#1D2939]' : ''}`}
              >
                {/* Icon */}
                <div className="flex h-8 w-8 flex-shrink-0 items-center justify-center rounded-full bg-[#12B76A]/15 mt-0.5">
                  <svg width="14" height="14" viewBox="0 0 12 12" fill="none" stroke="#12B76A" strokeWidth="2.5" strokeLinecap="round">
                    <path d="M2 6l3 3 5-5" />
                  </svg>
                </div>

                {/* Content */}
                <div className="flex-1 min-w-0 space-y-0.5">
                  {/* Movie */}
                  <p className="text-sm font-medium text-[#F9FAFB]">
                    {entry.movieTitle}
                    {entry.movieYear && (
                      <span className="ml-1.5 font-normal text-[#667085]">({entry.movieYear})</span>
                    )}
                  </p>

                  {/* Theme song */}
                  {entry.themeTitle && (
                    <p className="text-xs text-[#D0D5DD] flex items-center gap-1">
                      <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className="flex-shrink-0 text-[#475467]">
                        <path d="M9 18V5l12-2v13" />
                        <circle cx="6" cy="18" r="3" /><circle cx="18" cy="16" r="3" />
                      </svg>
                      {entry.sourceUrl ? (
                        <a
                          href={entry.sourceUrl}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="hover:text-[#CC3333] transition-colors truncate"
                        >
                          {entry.themeTitle}
                        </a>
                      ) : (
                        <span className="truncate">{entry.themeTitle}</span>
                      )}
                    </p>
                  )}

                  {/* Date */}
                  <p className="text-xs text-[#475467]">{formatDate(entry.downloadedAt)}</p>
                </div>
              </div>
            ))}
          </div>
          )}
        </div>
      )}
    </AppShell>
  )
}

function formatDate(iso: string): string {
  try {
    const d = new Date(iso)
    return d.toLocaleString(undefined, {
      year: 'numeric', month: 'short', day: 'numeric',
      hour: '2-digit', minute: '2-digit',
    })
  } catch {
    return iso
  }
}
