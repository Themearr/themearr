'use client'

import { useState } from 'react'
import Image from 'next/image'
import type { Movie } from '@/lib/types'
import { EmptyState } from '@/components/ui'
import { SearchModal } from './SearchModal'

interface MovieGridProps {
  movies: Movie[]
  onMovieUpdated: (movieId: string) => void
}

type Filter = 'all' | 'pending' | 'downloaded'

export function MovieGrid({ movies, onMovieUpdated }: MovieGridProps) {
  const [filter, setFilter]   = useState<Filter>('all')
  const [search, setSearch]   = useState('')
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
              className={`rounded-md px-3 py-1.5 text-xs font-medium transition-all
                ${filter === val
                  ? 'bg-[#1D2939] text-[#F9FAFB] shadow-sm'
                  : 'text-[#667085] hover:text-[#D0D5DD]'}`}
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

      {/* Grid */}
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
        <div className="grid grid-cols-3 gap-3 sm:grid-cols-4 md:grid-cols-5 lg:grid-cols-6 xl:grid-cols-8 2xl:grid-cols-10">
          {visible.map(movie => (
            <MovieCard
              key={movie.id}
              movie={movie}
              onClick={() => movie.status === 'pending' && setSelected(movie)}
            />
          ))}
        </div>
      )}

      {selected && (
        <SearchModal
          movie={selected}
          onClose={() => setSelected(null)}
          onDownloaded={id => { onMovieUpdated(id); setSelected(null) }}
        />
      )}
    </>
  )
}

function MovieCard({ movie, onClick }: { movie: Movie; onClick: () => void }) {
  const [imgError, setImgError] = useState(false)
  const isPending = movie.status === 'pending'

  return (
    <button
      onClick={onClick}
      className={`group relative flex flex-col text-left focus:outline-none ${isPending ? 'cursor-pointer' : 'cursor-default'}`}
    >
      {/* Poster */}
      <div className="relative w-full overflow-hidden rounded-lg bg-[#1D2939]" style={{ aspectRatio: '2/3' }}>
        {movie.posterUrl && !imgError ? (
          <Image
            src={movie.posterUrl}
            alt={movie.title}
            fill
            sizes="(max-width: 640px) 33vw, (max-width: 768px) 25vw, (max-width: 1024px) 20vw, (max-width: 1280px) 16vw, 12vw"
            className="object-cover transition-transform duration-200 group-hover:scale-105"
            onError={() => setImgError(true)}
            unoptimized
          />
        ) : (
          <div className="flex h-full w-full flex-col items-center justify-center gap-2 p-2">
            <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="#344054" strokeWidth="1.5" strokeLinecap="round">
              <rect x="2" y="2" width="20" height="20" rx="2" />
              <path d="M7 2v20M17 2v20M2 12h20" />
            </svg>
            <span className="text-center text-[10px] leading-tight text-[#475467] line-clamp-3">{movie.title}</span>
          </div>
        )}

        {/* Hover overlay for pending */}
        {isPending && (
          <div className="absolute inset-0 flex items-center justify-center rounded-lg bg-black/60 opacity-0 transition-opacity duration-200 group-hover:opacity-100">
            <div className="flex items-center gap-1.5 rounded-md bg-[#BB0000] px-3 py-1.5 text-xs font-medium text-white">
              <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round">
                <circle cx="11" cy="11" r="8" /><path d="m21 21-4.35-4.35" />
              </svg>
              Get theme
            </div>
          </div>
        )}

        {/* Downloaded checkmark badge */}
        {movie.status === 'downloaded' && (
          <div className="absolute bottom-1.5 right-1.5 flex h-5 w-5 items-center justify-center rounded-full bg-[#12B76A]">
            <svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round">
              <path d="M2 6l3 3 5-5" />
            </svg>
          </div>
        )}
      </div>

      {/* Title + year */}
      <div className="mt-1.5 px-0.5">
        <p className="truncate text-xs font-medium text-[#D0D5DD]">{movie.title}</p>
        {movie.year && (
          <p className="text-[11px] text-[#475467]">{movie.year}</p>
        )}
      </div>
    </button>
  )
}
