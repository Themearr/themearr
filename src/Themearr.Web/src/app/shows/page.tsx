import { useCallback, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { showsApi, settingsApi, systemApi } from '@/lib/api'
import type { Show } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { MediaGrid } from '@/components/media/MediaGrid'
import { showsAdapter } from '@/lib/media/adapter'
import { Button, EmptyState, ErrorIcon, Spinner } from '@/components/ui'
import { useResource } from '@/lib/useResource'

export default function ShowsPage() {
  const [shows, setShows] = useState<Show[]>([])
  const [syncing, setSyncing] = useState(false)
  const [syncError, setSyncError] = useState<string | null>(null)
  /** What the last sync reported — the task registry's own string, e.g. "synced 253 shows". */
  const [syncResult, setSyncResult] = useState<string | null>(null)
  const [refreshError, setRefreshError] = useState<string | null>(null)

  // Same monotonic-stamp guard the movies page uses: the sync flow and the initial load
  // can both be in flight, and a slower earlier response must not overwrite a newer one.
  const loadSeq = useRef(0)
  const loadShows = useCallback(async () => {
    const mine = ++loadSeq.current
    try {
      const list = await showsApi.list()
      if (mine !== loadSeq.current) return
      setShows(list)
      setRefreshError(null)
    } catch (e) {
      if (mine !== loadSeq.current) return
      setRefreshError(e instanceof Error && e.message ? e.message : 'Request failed')
    }
  }, [])

  // The initial load goes through useResource, like the movies page: it keeps "failed"
  // distinct from "empty", so an outage can't render as a reassuring "no shows yet".
  const loadInitialShows = useCallback(async () => {
    const list = await showsApi.list()
    setShows(list)
    setRefreshError(null)
    return list
  }, [])
  const { error: showsError, retry: retryShows } = useResource(loadInitialShows)

  // Whether any show library is selected decides between "no shows yet" and the
  // actionable "you haven't opted in" empty state.
  const loadHasLibraries = useCallback(async () => {
    try {
      const s = await settingsApi.get()
      return Object.values(s.selectedShowLibraries ?? {}).some(v => v.length > 0)
    } catch {
      // Don't accuse the operator of misconfiguring on a failed read — assume opted in.
      return true
    }
  }, [])
  const { data: librariesSelected } = useResource(loadHasLibraries)

  // Reads the shared task snapshot rather than a shows-specific status endpoint. Silent
  // on failure: this poll doesn't drive the page's content, so a dropped request must not
  // disturb what's already shown.
  //
  // Returns the task's own last result once it stops running, or null if we gave up
  // waiting. The caller must distinguish those: reporting a timeout as a completed sync
  // is the same silent-success problem that hid the show-sync bug for two releases.
  async function pollUntilSyncFinishes(): Promise<string | null> {
    for (let i = 0; i < 150; i++) {                 // ~5 minutes at 2s
      await new Promise(r => setTimeout(r, 2000))
      try {
        const row = (await systemApi.tasks()).find(t => t.id === 'syncShows')
        if (row && !row.isRunning) return row.lastResult ?? 'Sync finished'
      } catch { /* keep waiting */ }
    }
    return null
  }

  async function runSync() {
    setSyncing(true)
    setSyncError(null)
    setSyncResult(null)
    try {
      await systemApi.runTask('syncShows')
      const result = await pollUntilSyncFinishes()
      await loadShows()
      setSyncResult(result ?? 'Still syncing — it is taking longer than expected. Check System → Tasks.')
    } catch (e) {
      // A sync the operator explicitly asked for, so its failure must be visible.
      setSyncError(e instanceof Error && e.message ? e.message : 'Could not start the sync')
    } finally {
      setSyncing(false)
    }
  }

  return (
    <AppShell
      title="Shows"
      actions={<Button size="sm" onClick={runSync} loading={syncing}>Sync shows</Button>}
    >
      {syncError && (
        <div className="mb-4 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
          <p className="text-sm text-[#FDA29B]">{syncError}</p>
        </div>
      )}
      {refreshError && (
        <div className="mb-4 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
          <p className="text-sm text-[#FDA29B]">Couldn&apos;t refresh shows: {refreshError}</p>
        </div>
      )}

      {syncResult && !syncing && (
        <p className="mb-4 text-sm text-[#12B76A]">{syncResult}</p>
      )}

      {/* Checked before the empty states below. A show sync makes one Plex request per
          show, so it is slow enough that rendering "No shows yet" underneath a spinning
          button reads as a broken button rather than as work in progress. */}
      {syncing ? (
        <EmptyState
          icon={<Spinner size={28} className="text-[#BB0000]" />}
          title="Syncing shows from Plex…"
          description="Themearr asks Plex where each show lives, so a large library can take a minute."
        />
      ) : shows.length === 0 && showsError ? (
        // Nothing loaded AND the request failed — an outage must not render as a
        // reassuring "no shows yet". See useResource.
        <EmptyState
          icon={<ErrorIcon />}
          title="Couldn't load your shows"
          description={showsError}
          action={<Button variant="secondary" size="sm" onClick={retryShows}>Retry</Button>}
        />
      ) : shows.length === 0 && librariesSelected === false ? (
        <EmptyState
          icon={<ErrorIcon />}
          title="No show libraries selected"
          description="Themearr only syncs the Plex show libraries you choose."
          action={
            <Link to="/settings" className="text-sm text-[#CC3333] hover:underline">
              Choose them in Settings →
            </Link>
          }
        />
      ) : (
        <MediaGrid
          items={shows}
          adapter={showsAdapter}
          onUpdated={(id, status) =>
            setShows(prev => prev.map(s => (s.id === id ? { ...s, status } : s)))}
          emptyDescription="Sync your Plex show libraries to get started"
        />
      )}
    </AppShell>
  )
}
