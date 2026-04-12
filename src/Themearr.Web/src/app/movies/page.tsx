'use client'

import { useCallback, useEffect, useState } from 'react'
import { moviesApi, syncApi } from '@/lib/api'
import type { Movie, SyncStatus } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { MovieGrid } from '@/components/movies/MovieGrid'
import { Button, Spinner } from '@/components/ui'

export default function MoviesPage() {
  const [movies, setMovies]   = useState<Movie[]>([])
  const [loading, setLoading] = useState(true)
  const [sync, setSync]       = useState<SyncStatus | null>(null)
  const [syncing, setSyncing] = useState(false)

  const loadMovies = useCallback(async () => {
    try { setMovies(await moviesApi.list()) } catch { /* ignore */ }
  }, [])

  useEffect(() => {
    moviesApi.list()
      .then(movies => {
        setMovies(movies)
        // Auto-sync on first load if no movies have been synced yet
        if (movies.length === 0) {
          setSyncing(true)
          syncApi.start().catch(() => setSyncing(false))
        }
      })
      .finally(() => setLoading(false))
  }, [])

  // Poll sync status while in progress
  useEffect(() => {
    if (!syncing) return
    const id = setInterval(async () => {
      try {
        const status = await syncApi.status()
        setSync(status)
        if (status.finished) { setSyncing(false); loadMovies() }
      } catch { /* ignore */ }
    }, 1500)
    return () => clearInterval(id)
  }, [syncing, loadMovies])

  async function startSync() {
    setSyncing(true)
    setSync(null)
    try { await syncApi.start() } catch { setSyncing(false) }
  }

  function handleMovieUpdated(id: string) {
    setMovies(prev => prev.map(m => m.id === id ? { ...m, status: 'downloaded' } : m))
  }

  const pending    = movies.filter(m => m.status === 'pending').length
  const downloaded = movies.filter(m => m.status === 'downloaded').length

  return (
    <AppShell
      title="Movies"
      actions={
        <Button onClick={startSync} loading={syncing} variant="secondary" size="sm">
          {syncing ? 'Syncing…' : 'Sync Plex'}
        </Button>
      }
    >
      {/* Stats row */}
      {movies.length > 0 && (
        <div className="mb-5 flex gap-4">
          {[
            { label: 'Total',      value: movies.length, color: '#98A2B3' },
            { label: 'Downloaded', value: downloaded,    color: '#12B76A' },
            { label: 'Pending',    value: pending,       color: '#F79009' },
          ].map(({ label, value, color }) => (
            <div key={label} className="rounded-lg border border-[#1D2939] bg-[#101828] px-4 py-3">
              <p className="text-xs text-[#667085]">{label}</p>
              <p className="text-xl font-bold" style={{ color }}>{value}</p>
            </div>
          ))}
        </div>
      )}

      {/* Sync progress */}
      {syncing && sync && (
        <div className="mb-5 rounded-xl border border-[#344054]/40 bg-[#1D2939]/40 p-4 space-y-2">
          <div className="flex items-center gap-2 text-sm text-[#D0D5DD]">
            <Spinner size={14} />
            Syncing with Plex…
          </div>
          {sync.logs.length > 0 && (
            <div className="max-h-36 overflow-y-auto rounded-lg bg-[#0C111D] px-3 py-2">
              {sync.logs.slice(-20).map((line, i) => (
                <p key={i} className="font-mono text-xs text-[#667085] leading-relaxed">{line}</p>
              ))}
            </div>
          )}
        </div>
      )}

      {/* Content */}
      {loading ? (
        <div className="flex items-center justify-center py-24">
          <Spinner size={28} className="text-[#BB0000]" />
        </div>
      ) : (
        <MovieGrid movies={movies} onMovieUpdated={handleMovieUpdated} />
      )}
    </AppShell>
  )
}
