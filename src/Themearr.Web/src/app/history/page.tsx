'use client'

import { useEffect, useState } from 'react'
import { historyApi } from '@/lib/api'
import type { HistoryEntry } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { Spinner } from '@/components/ui'

export default function HistoryPage() {
  const [entries, setEntries] = useState<HistoryEntry[] | null>(null)

  useEffect(() => {
    historyApi.get().then(setEntries).catch(() => setEntries([]))
  }, [])

  return (
    <AppShell title="History">
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
        <div className="max-w-2xl">
          <p className="mb-4 text-sm text-[#667085]">{entries.length} theme{entries.length !== 1 ? 's' : ''} downloaded</p>
          <div className="rounded-xl border border-[#1D2939] overflow-hidden">
            {entries.map((entry, i) => (
              <div
                key={entry.id}
                className={`flex items-start gap-4 px-5 py-4 ${i < entries.length - 1 ? 'border-b border-[#1D2939]' : ''}`}
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
