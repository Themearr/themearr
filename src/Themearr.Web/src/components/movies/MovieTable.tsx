'use client'

import { useState } from 'react'
import type { Movie } from '@/lib/types'
import { Badge, Button, EmptyState } from '@/components/ui'
import { SearchModal } from './SearchModal'

interface MovieTableProps {
  movies: Movie[]
  onMovieUpdated: (movieId: string) => void
}

type Filter = 'all' | 'pending' | 'downloaded'

export function MovieTable({ movies, onMovieUpdated }: MovieTableProps) {
  const [filter, setFilter] = useState<Filter>('all')
  const [search, setSearch] = useState('')
  const [selected, setSelected] = useState<Movie | null>(null)

  const pending    = movies.filter(m => m.status === 'pending').length
  const downloaded = movies.filter(m => m.status === 'downloaded').length

  const visible = movies.filter(m => {
    if (filter === 'pending'    && m.status !== 'pending')    return false
    if (filter === 'downloaded' && m.status !== 'downloaded') return false
    if (search.trim()) {
      const q = search.toLowerCase()
      return m.title.toLowerCase().includes(q) || String(m.year ?? '').includes(q)
    }
    return true
  })

  return (
    <>
      {/* Toolbar */}
      <div className="mb-5 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        {/* Filter pills */}
        <div className="flex items-center gap-1 rounded-lg bg-[#101828] border border-[#1D2939] p-1">
          {([
            ['all',        `All (${movies.length})`],
            ['pending',    `Pending (${pending})`],
            ['downloaded', `Downloaded (${downloaded})`],
          ] as [Filter, string][]).map(([val, label]) => (
            <button
              key={val}
              onClick={() => setFilter(val)}
              className={`
                rounded-md px-3 py-1.5 text-xs font-medium transition-all
                ${filter === val
                  ? 'bg-[#1D2939] text-[#F9FAFB] shadow-sm'
                  : 'text-[#667085] hover:text-[#D0D5DD]'}
              `}
            >
              {label}
            </button>
          ))}
        </div>

        {/* Search */}
        <div className="relative">
          <svg className="absolute left-3 top-1/2 -translate-y-1/2 text-[#475467]" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <circle cx="11" cy="11" r="8" /><path d="m21 21-4.35-4.35" />
          </svg>
          <input
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="Search movies…"
            className="rounded-lg border border-[#344054] bg-[#101828] py-2 pl-9 pr-3.5 text-sm text-[#F9FAFB] placeholder:text-[#475467] outline-none focus:border-[#BB0000] focus:ring-1 focus:ring-[#BB0000]/40 w-56"
          />
        </div>
      </div>

      {/* Table */}
      {visible.length === 0 ? (
        <EmptyState
          icon={
            <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round">
              <rect x="2" y="2" width="20" height="20" rx="2" />
              <path d="M7 2v20M17 2v20M2 12h20" />
            </svg>
          }
          title={search ? 'No movies match your search' : 'No movies yet'}
          description={search ? 'Try a different search term' : 'Sync your Plex library to get started'}
        />
      ) : (
        <div className="rounded-xl border border-[#1D2939] overflow-hidden">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-[#1D2939] bg-[#101828]">
                <th className="px-4 py-3 text-left text-xs font-medium text-[#667085] uppercase tracking-wider">Title</th>
                <th className="px-4 py-3 text-left text-xs font-medium text-[#667085] uppercase tracking-wider w-20">Year</th>
                <th className="px-4 py-3 text-left text-xs font-medium text-[#667085] uppercase tracking-wider w-32">Status</th>
                <th className="px-4 py-3 w-28" />
              </tr>
            </thead>
            <tbody className="divide-y divide-[#1D2939]">
              {visible.map((movie, i) => (
                <tr
                  key={movie.id}
                  className={`group transition-colors hover:bg-[#101828] ${i % 2 === 0 ? 'bg-[#0C111D]' : 'bg-[#0e1520]'}`}
                >
                  <td className="px-4 py-3.5 font-medium text-[#F9FAFB]">{movie.title}</td>
                  <td className="px-4 py-3.5 text-[#667085]">{movie.year ?? '—'}</td>
                  <td className="px-4 py-3.5">
                    {movie.status === 'downloaded'
                      ? <Badge variant="success">Downloaded</Badge>
                      : <Badge variant="warning">Pending</Badge>}
                  </td>
                  <td className="px-4 py-3.5 text-right">
                    {movie.status === 'pending' && (
                      <Button
                        variant="secondary"
                        size="sm"
                        onClick={() => setSelected(movie)}
                        className="opacity-0 group-hover:opacity-100 transition-opacity"
                      >
                        Get theme
                      </Button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* Modal */}
      {selected && (
        <SearchModal
          movie={selected}
          onClose={() => setSelected(null)}
          onDownloaded={id => {
            onMovieUpdated(id)
            setSelected(null)
          }}
        />
      )}
    </>
  )
}
