import { useCallback, useEffect, useState } from 'react'
import { moviesApi, radarrApi, syncApi } from '@/lib/api'
import type { Movie, SyncStatus } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { MovieGrid } from '@/components/movies/MovieGrid'
import { Button, EmptyState, Spinner } from '@/components/ui'
import { useResource } from '@/lib/useResource'

// Shown when the initial load fails, so a network/server error never gets
// mistaken for "you have no movies".
const ERROR_ICON = (
  <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M12 9v4" />
    <path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0Z" />
    <path d="M12 17h.01" />
  </svg>
)

export default function MoviesPage() {
  const [movies, setMovies]   = useState<Movie[]>([])
  const [sync, setSync]       = useState<SyncStatus | null>(null)
  const [syncing, setSyncing] = useState(false)
  const [source, setSource]   = useState<'plex' | 'radarr'>('plex')

  // Used only by the sync-status poll below, to silently refresh the list once
  // a sync finishes. Left untouched: a dropped refresh there should stay quiet
  // rather than blank an already-populated grid.
  const loadMovies = useCallback(async () => {
    try { setMovies(await moviesApi.list()) } catch { /* ignore */ }
  }, [])

  // The initial load. Routed through useResource so a failed request surfaces
  // as an error screen instead of an empty library. Success also seeds the
  // mutable `movies` copy the rest of the page reads/updates, and triggers an
  // auto-sync when the library comes back genuinely empty -- done here, inside
  // the fetcher, rather than in a second effect derived from the result.
  const loadInitialMovies = useCallback(async () => {
    const list = await moviesApi.list()
    setMovies(list)
    if (list.length === 0) {
      setSyncing(true)
      syncApi.start().catch(() => setSyncing(false))
    }
    return list
  }, [])
  const { data: loadedMovies, error: moviesError, retry: retryMovies } = useResource(loadInitialMovies)

  // Learn the active library source so the sync control doesn't hardcode "Plex".
  useEffect(() => {
    radarrApi.get().then(s => setSource(s.source)).catch(() => { /* default to plex */ })
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

  function handleMovieUpdated(id: string, status: Movie['status']) {
    setMovies(prev => prev.map(m => m.id === id ? { ...m, status } : m))
  }

  const pending    = movies.filter(m => m.status === 'pending').length
  const downloaded = movies.filter(m => m.status === 'downloaded').length
  const sourceLabel = source === 'radarr' ? 'Radarr' : 'Plex'

  return (
    <AppShell
      title="Movies"
      actions={
        <Button onClick={startSync} loading={syncing} variant="secondary" size="sm">
          {syncing ? 'Syncing…' : `Sync ${sourceLabel}`}
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
            Syncing with {sourceLabel}…
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
      {loadedMovies === null && moviesError ? (
        <EmptyState
          icon={ERROR_ICON}
          title="Couldn&apos;t load your movies"
          description={moviesError}
          action={<Button variant="secondary" size="sm" onClick={retryMovies}>Retry</Button>}
        />
      ) : loadedMovies === null ? (
        <div className="flex items-center justify-center py-24">
          <Spinner size={28} className="text-[#BB0000]" />
        </div>
      ) : (
        <>
          {moviesError && (
            <div className="mb-5 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
              <p className="text-sm text-[#FDA29B]">Couldn&apos;t refresh movies: {moviesError}</p>
            </div>
          )}
          <MovieGrid movies={movies} onMovieUpdated={handleMovieUpdated} sourceLabel={sourceLabel} />
        </>
      )}
    </AppShell>
  )
}
