import { useCallback, useEffect, useRef, useState } from 'react'
import { moviesApi, radarrApi, syncApi } from '@/lib/api'
import type { MediaStatus, Movie, SyncStatus } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { MediaGrid } from '@/components/media/MediaGrid'
import { moviesAdapter } from '@/lib/media/adapter'
import { Button, EmptyState, ErrorIcon, Spinner } from '@/components/ui'
import { useResource } from '@/lib/useResource'

export default function MoviesPage() {
  const [movies, setMovies]   = useState<Movie[]>([])
  const [sync, setSync]       = useState<SyncStatus | null>(null)
  const [syncing, setSyncing] = useState(false)
  const [source, setSource]   = useState<'plex' | 'radarr'>('plex')
  // Set when a refresh *after* the page already has data fails -- currently
  // only the sync-status poll's post-sync reload below. Distinct from the
  // initial-load error tracked by `useResource`: by the time this can be set,
  // the grid already has something to show (even if that's a confirmed-empty
  // `[]`), so a failure here must never blank it -- only flag that what's
  // shown may be stale.
  const [refreshError, setRefreshError] = useState<string | null>(null)
  const [syncError, setSyncError] = useState<string | null>(null)
  // True once *any* fetch -- the initial load or a later refresh -- has
  // produced real data. Normally this tracks `useResource`'s own `data`, but
  // it can also flip true when a refresh succeeds after the initial load
  // failed (e.g. the user manually retriggers a sync), which `useResource`
  // has no way to reflect back onto its own `data`/`error`.
  const [hasData, setHasData] = useState(false)

  // Called by the sync-status poll below once a sync finishes, to refresh the
  // grid with whatever it imported. Unlike the poll's own `syncApi.status`
  // call (left silent on purpose -- a dropped status check must not disturb
  // the page), this refresh is a direct consequence of a sync the user
  // started, so its failure must be visible: it's surfaced as a non-blocking
  // notice via `refreshError` rather than swallowed. It still never blanks
  // `movies` -- a failed refresh just leaves the last known list in place.
  // Monotonic stamp so a slower earlier refresh can't overwrite a newer one --
  // the Retry button and the sync poll both call this, and neither response is
  // otherwise ordered. Same `latest`-ref technique useResource (src/lib/
  // useResource.ts) uses: each call claims a number at issue time and only
  // writes state if it's still the newest issued when it resolves.
  const loadSeq = useRef(0)
  const loadMovies = useCallback(async () => {
    const mine = ++loadSeq.current
    try {
      const list = await moviesApi.list()
      if (mine !== loadSeq.current) return
      setMovies(list)
      setHasData(true)
      setRefreshError(null)
    } catch (e) {
      if (mine !== loadSeq.current) return
      setRefreshError(e instanceof Error && e.message ? e.message : 'Request failed')
    }
  }, [])

  // The Retry buttons on a failed refresh, wrapping `loadMovies` so repeated
  // clicks can't put several `GET /api/movies` in flight at once -- there's no
  // staleness protection on those responses, so a slower earlier one would
  // overwrite a newer one. Deliberately a wrapper rather than a guard inside
  // `loadMovies` itself: the sync-status poll's own post-sync refresh must
  // still run on its own terms, since skipping it in favour of a retry started
  // *before* the sync finished would show a list that predates the import.
  //
  // That argument only rules out a *guard* (something that drops a call
  // outright) -- it leaves a residual race between this Retry and the poll's
  // own `loadMovies` call, since neither response is stamped, so a slower one
  // can still overwrite a newer one. The right fix for that residual, if it's
  // ever worth closing, is a monotonic sequence stamp -- the same `latest`-ref
  // pattern `useResource` (src/lib/useResource.ts) already uses -- which
  // discards a response older than the newest *issued* rather than skipping a
  // call outright, so it isn't subject to the objection above. Not done here:
  // it would mean touching the poll path, which is out of scope for this fix.
  const [retrying, setRetrying] = useState(false)
  async function retryLoadMovies() {
    if (retrying) return
    setRetrying(true)
    try { await loadMovies() } finally { setRetrying(false) }
  }

  // The initial load. Routed through useResource so a failed request surfaces
  // as an error screen instead of an empty library. Success also seeds the
  // mutable `movies` copy the rest of the page reads/updates, and triggers an
  // auto-sync when the library comes back genuinely empty -- done here, inside
  // the fetcher, rather than in a second effect derived from the result.
  const loadInitialMovies = useCallback(async () => {
    const list = await moviesApi.list()
    setMovies(list)
    setHasData(true)
    // This load just succeeded, so any earlier refresh failure is stale news.
    // Left set, it would report a confirmed-empty library as a failed refresh.
    setRefreshError(null)
    if (list.length === 0) {
      setSyncing(true)
      syncApi.start().catch(() => setSyncing(false))
    }
    return list
  }, [])
  const { error: moviesError, retry: retryMovies } = useResource(loadInitialMovies)

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
    setSyncError(null)
    try {
      await syncApi.start()
    } catch (e) {
      // Unlike the empty-library auto-sync above (deliberately silent), this
      // sync is one the user explicitly asked for, so its failure must show.
      setSyncing(false)
      setSyncError(e instanceof Error && e.message ? e.message : 'Request failed')
    }
  }

  // Takes the full MediaStatus because MediaGrid is shared with shows. A movie can never
  // actually be 'plexTheme' — the API only ever reports that for shows — but the callback
  // has to accept the wider type to be assignable to the grid's prop.
  function handleMovieUpdated(id: string, status: MediaStatus) {
    setMovies(prev => prev.map(m => m.id === id ? { ...m, status: status as Movie['status'] } : m))
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

      {/* A failed manual sync must not vanish silently. */}
      {syncError && (
        <div className="mb-5 flex items-center justify-between gap-3 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
          <p className="text-sm text-[#FDA29B]">Couldn&apos;t start sync: {syncError}</p>
          <Button variant="secondary" size="sm" onClick={startSync}>Retry</Button>
        </div>
      )}

      {/* Content */}
      {!hasData && moviesError ? (
        <EmptyState
          icon={<ErrorIcon />}
          title="Couldn&apos;t load your movies"
          description={moviesError}
          action={<Button variant="secondary" size="sm" onClick={retryMovies}>Retry</Button>}
        />
      ) : !hasData ? (
        <div className="flex items-center justify-center py-24">
          <Spinner size={28} className="text-[#BB0000]" />
        </div>
      ) : movies.length === 0 && refreshError ? (
        // We have data (the page loaded successfully at some point), but the
        // most recent refresh -- the one that would confirm whether the
        // library is genuinely empty -- failed. Showing "No movies yet" here
        // would be exactly the lie this page exists to avoid, so this stays
        // an explicit "couldn't confirm" state instead.
        <EmptyState
          icon={<ErrorIcon />}
          title="Couldn&apos;t refresh your movies"
          description={`${refreshError} — your list may be out of date.`}
          action={<Button variant="secondary" size="sm" onClick={retryLoadMovies} loading={retrying}>Retry</Button>}
        />
      ) : (
        <>
          {refreshError && (
            <div className="mb-5 flex items-center justify-between gap-3 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
              <p className="text-sm text-[#FDA29B]">Couldn&apos;t refresh movies: {refreshError} — your list may be out of date.</p>
              <Button variant="secondary" size="sm" onClick={retryLoadMovies} loading={retrying}>Retry</Button>
            </div>
          )}
          <MediaGrid
            items={movies}
            adapter={moviesAdapter}
            onUpdated={handleMovieUpdated}
            emptyDescription={`Sync your ${sourceLabel} library to get started`}
          />
        </>
      )}
    </AppShell>
  )
}
