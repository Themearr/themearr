'use client'

import { useEffect, useState } from 'react'
import Link from 'next/link'
import { statsApi } from '@/lib/api'
import type { DashboardStats } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { Spinner } from '@/components/ui'

export default function DashboardPage() {
  const [stats,   setStats]   = useState<DashboardStats | null>(null)
  const [imgErrors, setImgErrors] = useState<Record<string, boolean>>({})

  useEffect(() => {
    statsApi.get().then(setStats).catch(() => null)
  }, [])

  if (!stats) {
    return (
      <AppShell title="Dashboard">
        <div className="flex justify-center py-24">
          <Spinner size={28} className="text-[#BB0000]" />
        </div>
      </AppShell>
    )
  }

  const coverageColor = stats.coverage >= 80 ? '#12B76A' : stats.coverage >= 40 ? '#F79009' : '#BB0000'

  return (
    <AppShell title="Dashboard">
      <div className="space-y-6">

        {/* ── Coverage hero ───────────────────────────────────────────── */}
        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-5">
          <div className="flex items-end justify-between mb-3">
            <div>
              <p className="text-xs font-semibold text-[#667085] uppercase tracking-wider mb-1">Library coverage</p>
              <p className="text-4xl font-bold" style={{ color: coverageColor }}>{stats.coverage}%</p>
            </div>
            <p className="text-sm text-[#667085] pb-1">
              {stats.downloaded} of {stats.total} movies
            </p>
          </div>
          {/* Progress bar */}
          <div className="h-2 w-full rounded-full bg-[#1D2939] overflow-hidden">
            <div
              className="h-full rounded-full transition-all duration-700"
              style={{ width: `${Math.min(stats.coverage, 100)}%`, backgroundColor: coverageColor }}
            />
          </div>
        </div>

        {/* ── Stat cards ──────────────────────────────────────────────── */}
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          {[
            { label: 'Pending',    value: stats.pending,       color: '#F79009', href: '/queue' },
            { label: 'Downloaded', value: stats.downloaded,    color: '#12B76A', href: '/movies' },
            { label: 'This week',  value: stats.addedThisWeek, color: '#6CE9A6', href: '/history' },
            { label: 'Ignored',    value: stats.ignored,       color: '#475467', href: '/movies' },
          ].map(({ label, value, color, href }) => (
            <Link
              key={label}
              href={href}
              className="rounded-xl border border-[#1D2939] bg-[#101828] px-4 py-4 hover:border-[#344054] transition-colors"
            >
              <p className="text-xs text-[#667085] mb-1">{label}</p>
              <p className="text-2xl font-bold" style={{ color }}>{value}</p>
            </Link>
          ))}
        </div>

        {/* ── Bottom panels ───────────────────────────────────────────── */}
        <div className="grid grid-cols-1 gap-5 lg:grid-cols-2">

          {/* Recent downloads */}
          <div className="rounded-xl border border-[#1D2939] bg-[#101828] overflow-hidden">
            <div className="flex items-center justify-between px-4 py-3 border-b border-[#1D2939]">
              <p className="text-xs font-semibold text-[#667085] uppercase tracking-wider">Recent downloads</p>
              <Link href="/history" className="text-xs text-[#CC3333] hover:text-[#E07777] transition-colors">View all →</Link>
            </div>
            {stats.recentActivity.length === 0 ? (
              <p className="px-4 py-6 text-sm text-[#475467]">No themes downloaded yet.</p>
            ) : (
              <div className="divide-y divide-[#1D2939]">
                {stats.recentActivity.map(entry => (
                  <div key={entry.id} className="flex items-start gap-3 px-4 py-3">
                    <div className="flex h-7 w-7 flex-shrink-0 items-center justify-center rounded-full bg-[#12B76A]/15 mt-0.5">
                      <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="#12B76A" strokeWidth="2.5" strokeLinecap="round">
                        <path d="M2 6l3 3 5-5" />
                      </svg>
                    </div>
                    <div className="flex-1 min-w-0">
                      <p className="text-sm font-medium text-[#F9FAFB] truncate">
                        {entry.movieTitle}
                        {entry.movieYear && <span className="ml-1.5 font-normal text-[#667085]">({entry.movieYear})</span>}
                      </p>
                      {entry.themeTitle && (
                        <p className="text-xs text-[#667085] truncate flex items-center gap-1">
                          <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className="flex-shrink-0">
                            <path d="M9 18V5l12-2v13" /><circle cx="6" cy="18" r="3" /><circle cx="18" cy="16" r="3" />
                          </svg>
                          {entry.themeTitle}
                        </p>
                      )}
                      <p className="text-[11px] text-[#475467]">{formatDate(entry.downloadedAt)}</p>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>

          {/* Recently added to queue */}
          <div className="rounded-xl border border-[#1D2939] bg-[#101828] overflow-hidden">
            <div className="flex items-center justify-between px-4 py-3 border-b border-[#1D2939]">
              <p className="text-xs font-semibold text-[#667085] uppercase tracking-wider">Recently added</p>
              <Link href="/queue" className="text-xs text-[#CC3333] hover:text-[#E07777] transition-colors">Go to queue →</Link>
            </div>
            {stats.recentlyAdded.length === 0 ? (
              <p className="px-4 py-6 text-sm text-[#475467]">
                {stats.pending === 0 ? 'All movies have themes!' : 'Sync Plex to populate.'}
              </p>
            ) : (
              <div className="divide-y divide-[#1D2939]">
                {stats.recentlyAdded.map(movie => (
                  <div key={movie.id} className="flex items-center gap-3 px-4 py-2.5">
                    {/* Mini poster */}
                    <div className="relative h-10 w-7 flex-shrink-0 rounded overflow-hidden bg-[#1D2939]">
                      {movie.posterUrl && !imgErrors[movie.id] ? (
                        <img
                          src={movie.posterUrl}
                          alt={movie.title}
                          className="absolute inset-0 h-full w-full object-cover"
                          onError={() => setImgErrors(e => ({ ...e, [movie.id]: true }))}
                          loading="lazy"
                        />
                      ) : (
                        <div className="flex h-full w-full items-center justify-center">
                          <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="#344054" strokeWidth="1.5" strokeLinecap="round">
                            <rect x="2" y="2" width="20" height="20" rx="2" /><path d="M7 2v20M17 2v20M2 12h20" />
                          </svg>
                        </div>
                      )}
                    </div>
                    <div className="flex-1 min-w-0">
                      <p className="text-sm text-[#D0D5DD] truncate">{movie.title}</p>
                      {movie.year && <p className="text-[11px] text-[#475467]">{movie.year}</p>}
                    </div>
                    <div className="flex-shrink-0 h-1.5 w-1.5 rounded-full bg-[#F79009]" title="Pending" />
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>

      </div>
    </AppShell>
  )
}

function formatDate(iso: string): string {
  try {
    const d = new Date(iso)
    const now = new Date()
    const diffMs = now.getTime() - d.getTime()
    const diffH  = Math.floor(diffMs / (1000 * 60 * 60))
    const diffD  = Math.floor(diffH / 24)
    if (diffH < 1)  return 'Just now'
    if (diffH < 24) return `${diffH}h ago`
    if (diffD < 7)  return `${diffD}d ago`
    return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
  } catch {
    return iso
  }
}
