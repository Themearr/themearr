import { useCallback, useEffect, useRef, useState } from 'react'
import { moviesApi, settingsApi } from '@/lib/api'
import type { Movie, YoutubeResult } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { Button, EmptyState, ErrorIcon, Input, Spinner } from '@/components/ui'
import { useResource } from '@/lib/useResource'

// Bounds the polled `downloadStatus` request below (the "in-flight" guard's
// own comment explains what it's guarding against). `request()` in
// src/lib/api.ts is a bare `fetch` with no built-in timeout, so a request
// that never settles would hold the poll's `inFlight` flag forever -- and
// with it, Skip and Ignore, which both check `downloading`. The server side
// of this call (`DownloadStatus` in Themearr.API) is a plain in-memory
// lookup with no I/O, so a healthy round trip is well under a second; 8x the
// 1s poll interval is comfortably clear of that, while still short enough
// that a genuinely wedged queue unwedges itself in single-digit seconds --
// long before a person would give up and reload the page.
const STATUS_TIMEOUT_MS = 8000

// A single timed-out/dropped status check is transient -- the next tick recovers
// it. But if this many in a row fail, the server's effectively unreachable, and
// continuing to show "Downloading…" with Skip/Ignore disabled would wedge the
// queue behind a reload. At that point we give up tracking and hand control back.
const STATUS_MAX_FAILURES = 3

export default function QueuePage() {
  const [currentIdx,   setCurrentIdx]   = useState(0)
  const [results,      setResults]      = useState<YoutubeResult[]>([])
  const [searching,    setSearching]    = useState(false)
  const [searchQuery,  setSearchQuery]  = useState('')
  const [manualUrl,    setManualUrl]    = useState('')
  const [error,        setError]        = useState('')
  const [downloading,   setDownloading]   = useState(false)
  const [downloadLogs,  setDownloadLogs]  = useState<string[]>([])
  const [autoMode,      setAutoMode]      = useState(false)
  const [savingAuto,    setSavingAuto]    = useState(false)

  // Holds the movieId being downloaded so the polling closure keeps the right id
  const downloadingMovieId = useRef<string | null>(null)
  const searchedFor        = useRef<string | null>(null)
  // Tracks whether we've already triggered auto-download for the current movie
  const autoTriggeredFor   = useRef<string | null>(null)
  // Keep a ref in sync with autoMode so polling closures never see stale state
  const autoModeRef        = useRef(autoMode)

  // The initial load. Routed through useResource so a failed request surfaces
  // as an error screen instead of "every movie already has a theme".
  const { data: movies, error: moviesError, retry: retryMovies } = useResource(useCallback(() => moviesApi.list(), []))
  const pending = movies ? movies.filter(m => m.status === 'pending') : null

  const current   = pending?.[currentIdx] ?? null
  const remaining = pending ? Math.max(0, pending.length - currentIdx) : 0

  // ── Load auto mode setting ──────────────────────────────────────────────────
  useEffect(() => {
    settingsApi.get()
      .then(s => setAutoMode(s.autoDownload))
      .catch(() => null)
  }, [])

  // Keep ref in sync
  useEffect(() => { autoModeRef.current = autoMode }, [autoMode])

  // Saves first and only flips the switch once the server confirms it, rather
  // than flipping optimistically and trying to unwind it on failure -- so the
  // control can never read "on" while the background auto-download worker
  // never actually started. Settings only exposes a whole-object get/save
  // (no narrower "just autoDownload" endpoint), so this still has to read the
  // rest of the settings back before writing them -- there's no way to avoid
  // that round trip without adding a backend route, which is out of scope
  // here.
  //
  // Because the switch doesn't move until the round trip finishes, a slow save
  // would otherwise look like a dead control -- no movement, no spinner, no
  // error -- and invite repeated clicks, each firing its own get+save pair.
  // `savingAuto` both disables the control and puts a spinner where the switch
  // is, so the wait is visible and only one save is ever in flight.
  async function toggleAutoMode() {
    if (savingAuto) return
    const next = !autoMode
    setSavingAuto(true)
    setError('')
    try {
      const s = await settingsApi.get()
      await settingsApi.save({ ...s, autoDownload: next })
      setAutoMode(next)
    } catch (e) {
      setError(`Couldn't turn auto mode ${next ? 'on' : 'off'}: ${(e as Error)?.message || 'unknown error'}`)
    } finally {
      setSavingAuto(false)
    }
  }

  // ── Auto-search when displayed movie changes ───────────────────────────────
  useEffect(() => {
    if (!current || searchedFor.current === current.id) return
    searchedFor.current = current.id
    setResults([])
    setError('')
    setManualUrl('')
    setSearchQuery('')
    setSearching(true)
    moviesApi.search(current.id)
      .then(data => setResults(data.results))
      .catch((e: Error) => setError(e.message))
      .finally(() => setSearching(false))
  }, [current])

  function reSearch(q?: string) {
    if (!current) return
    setResults([])
    setError('')
    setSearching(true)
    moviesApi.search(current.id, q || undefined)
      .then(data => setResults(data.results))
      .catch((e: Error) => setError(e.message))
      .finally(() => setSearching(false))
  }

  // Declared before its first use — a hoisted call from above reads as a stale
  // reference to React's compiler lint (react-hooks/immutability).
  //
  // `forMovieId` makes the call idempotent for callers that are advancing on
  // behalf of one specific download: the queue only moves if that download is
  // still the one in flight. A duplicate call — a second status poll that
  // resolved after the first already advanced — finds the ref cleared (or
  // pointing at the next movie) and does nothing. The in-flight guard in the
  // poll below should stop those duplicates ever happening; this is the second
  // line of defence, because the failure mode it prevents (a movie skipped with
  // no theme, no error and no trace) is completely silent. Callers acting on
  // the user's behalf — Skip, Ignore — pass nothing and always advance.
  function advanceQueue(forMovieId?: string) {
    if (forMovieId !== undefined && downloadingMovieId.current !== forMovieId) return
    setCurrentIdx((i: number) => i + 1)
    setResults([])
    setError('')
    setManualUrl('')
    setDownloading(false)
    setDownloadLogs([])
    downloadingMovieId.current = null
  }

  async function skipForever() {
    if (!current) return
    try {
      await moviesApi.ignoreMovie(current.id)
    } catch (e) {
      // The server didn't record the ignore, so advancing would hide a movie it
      // still has as pending -- it'd reappear on the next load, making the button
      // look like it did nothing. Surface the failure and stay on this movie.
      setError((e as Error).message)
      return
    }
    advanceQueue()
  }

  // ── Auto-download in auto mode ─────────────────────────────────────────────
  // Calls the server-side auto-download endpoint directly rather than waiting
  // for client-side search results — avoids silent failures from scoring edge cases.
  useEffect(() => {
    if (!autoMode || !current || downloading) return
    if (autoTriggeredFor.current === current.id) return

    autoTriggeredFor.current = current.id
    downloadingMovieId.current = current.id
    setDownloading(true)
    setError('')
    moviesApi.autoDownload(current.id)
      .catch((e: Error) => {
        setError(e.message)
        setDownloading(false)
        downloadingMovieId.current = null
        autoTriggeredFor.current = null // allow manual retry
      })
  }, [autoMode, current, downloading])

  // ── Poll download status while a download is in flight ────────────────────
  useEffect(() => {
    if (!downloading) return
    const movieId = downloadingMovieId.current
    if (!movieId) return

    // A status request that takes longer than the interval used to leave two
    // callbacks in flight at once. Both saw `finished`, and both advanced the
    // queue — the `clearInterval` in the first came too late for the second —
    // so a movie was silently skipped: no theme, no error, nothing to see. The
    // guard makes an overlapping tick a no-op. It's a plain local rather than a
    // ref so each run of this effect gets a fresh one: a request left hanging
    // by a previous download can never block the next download's polling.
    let inFlight = false
    // Counts consecutive failed status checks; any success resets it. Local to
    // this effect run so a fresh download always starts from a clean slate.
    let failures = 0

    const id = setInterval(async () => {
      if (inFlight) return
      inFlight = true

      // A request that never settles (dropped connection, a server stuck
      // mid-request) would otherwise hold `inFlight` forever with no recovery
      // path -- unlike a rejection, which the catch below already recovers
      // from. The abort turns "never settles" into "rejects after
      // STATUS_TIMEOUT_MS", so it lands in the same catch/finally and polling
      // resumes on schedule.
      const controller = new AbortController()
      const timeoutId = setTimeout(() => controller.abort(), STATUS_TIMEOUT_MS)

      try {
        const st = await moviesApi.downloadStatus(movieId, { signal: controller.signal })
        failures = 0 // a status came back -- we're still in contact with the server
        if (st.logs?.length) setDownloadLogs(st.logs)
        if (!st.finished) return
        clearInterval(id)
        if (st.error) {
          setError(st.error)
          setDownloading(false)
          // Auto mode: skip this movie automatically after a short pause
          if (autoModeRef.current) {
            setTimeout(() => {
              setError('')
              advanceQueue(movieId)
            }, 3000)
          }
        } else {
          advanceQueue(movieId)
        }
      } catch {
        // A transient drop/timeout: the next tick normally recovers it. But if
        // enough fail in a row the server is unreachable, and staying in the
        // "Downloading…" state would wedge the queue with Skip/Ignore disabled.
        // Give up tracking, say so, and re-enable the in-app escapes. We can't
        // know the download's real outcome, so we don't advance -- a reload (or
        // the next visit) will reflect whatever actually happened server-side.
        failures++
        if (failures >= STATUS_MAX_FAILURES) {
          clearInterval(id)
          setError('Lost contact with the server while tracking this download. It may still finish — reload to check.')
          setDownloading(false)
          downloadingMovieId.current = null
        }
      }
      finally {
        clearTimeout(timeoutId)
        inFlight = false
      }
    }, 1000)

    return () => clearInterval(id)
  }, [downloading])

  async function doDownload(videoId: string) {
    if (!current) return
    downloadingMovieId.current = current.id
    setDownloading(true)
    setError('')
    try {
      await moviesApi.download(current.id, videoId)
    } catch (e) {
      setError((e as Error).message)
      setDownloading(false)
      downloadingMovieId.current = null
    }
  }

  async function doDownloadUrl() {
    if (!current || !manualUrl.trim()) return
    downloadingMovieId.current = current.id
    setDownloading(true)
    setError('')
    try {
      await moviesApi.downloadUrl(current.id, manualUrl.trim())
    } catch (e) {
      setError((e as Error).message)
      setDownloading(false)
      downloadingMovieId.current = null
    }
  }

  async function doAutoDownload() {
    if (!current) return
    downloadingMovieId.current = current.id
    setDownloading(true)
    setError('')
    try {
      await moviesApi.autoDownload(current.id)
    } catch (e) {
      setError((e as Error).message)
      setDownloading(false)
      downloadingMovieId.current = null
    }
  }

  // ── Loading / failed ───────────────────────────────────────────────────────
  if (pending === null) {
    return (
      <AppShell title="Queue">
        {moviesError ? (
          <EmptyState
            icon={<ErrorIcon />}
            title="Couldn&apos;t load the queue"
            description={moviesError}
            action={<Button variant="secondary" size="sm" onClick={retryMovies}>Retry</Button>}
          />
        ) : (
          <div className="flex justify-center py-24">
            <Spinner size={28} className="text-[#BB0000]" />
          </div>
        )}
      </AppShell>
    )
  }

  // ── All done ───────────────────────────────────────────────────────────────
  if (!current) {
    return (
      <AppShell title="Queue">
        {moviesError && (
          <div className="mb-5 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
            <p className="text-sm text-[#FDA29B]">Couldn&apos;t refresh the queue: {moviesError}</p>
          </div>
        )}
        <div className="flex flex-col items-center justify-center py-24 gap-3 text-center">
          <div className="flex h-14 w-14 items-center justify-center rounded-full bg-[#12B76A]/15">
            <svg width="26" height="26" viewBox="0 0 24 24" fill="none" stroke="#12B76A" strokeWidth="2" strokeLinecap="round">
              <path d="M20 6 9 17l-5-5" />
            </svg>
          </div>
          <p className="text-base font-semibold text-[#F9FAFB]">All caught up!</p>
          <p className="text-sm text-[#667085]">Every movie in your library has a theme.</p>
        </div>
      </AppShell>
    )
  }

  const bestMatch = results.find(r => r.bestMatch)

  // ── Queue ──────────────────────────────────────────────────────────────────
  return (
    <AppShell
      title="Queue"
      actions={
        <div className="flex items-center gap-2">
          <button
            onClick={toggleAutoMode}
            disabled={savingAuto}
            className={`flex items-center gap-2 rounded-lg px-2.5 py-1.5 text-xs font-medium transition-colors disabled:cursor-not-allowed ${autoMode ? 'bg-[#12B76A]/15 text-[#12B76A]' : 'bg-[#1D2939] text-[#667085] hover:text-[#D0D5DD]'}`}
          >
            {savingAuto ? (
              <Spinner size={16} className="flex-shrink-0" />
            ) : (
              <span className={`relative inline-flex h-4 w-7 flex-shrink-0 rounded-full border-2 border-transparent transition-colors ${autoMode ? 'bg-[#12B76A]' : 'bg-[#344054]'}`}>
                <span className={`inline-block h-3 w-3 transform rounded-full bg-white shadow transition-transform ${autoMode ? 'translate-x-3' : 'translate-x-0'}`} />
              </span>
            )}
            Auto
          </button>
          <Button variant="ghost" size="sm" onClick={skipForever} disabled={downloading} title="Never show this movie in the queue again">
            Ignore
          </Button>
          {/* Wrapped, not passed directly: advanceQueue's first parameter is a
              movie id, and handing it the click event would make it a no-op. */}
          <Button variant="ghost" size="sm" onClick={() => advanceQueue()} disabled={downloading}>
            Skip
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
              <path d="M5 12h14M12 5l7 7-7 7" />
            </svg>
          </Button>
        </div>
      }
    >
      <div className="max-w-2xl space-y-5">

        {moviesError && (
          <div className="rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
            <p className="text-sm text-[#FDA29B]">Couldn&apos;t refresh the queue: {moviesError}</p>
          </div>
        )}

        {/* Movie card */}
        <div className="flex items-start gap-4 rounded-xl border border-[#1D2939] bg-[#101828] p-4">
          <MoviePoster movie={current} />
          <div className="flex-1 min-w-0 pt-0.5">
            <p className="text-base font-semibold text-[#F9FAFB] leading-snug">{current.title}</p>
            {current.year && <p className="text-sm text-[#667085] mt-0.5">{current.year}</p>}
            <p className="mt-2 text-xs text-[#475467]">
              {remaining} movie{remaining !== 1 ? 's' : ''} left in queue
            </p>
          </div>
        </div>

        {/* Up next */}
        {pending && pending.length > currentIdx + 1 && (
          <div className="rounded-xl border border-[#1D2939] bg-[#101828] overflow-hidden">
            <div className="px-4 py-3 border-b border-[#1D2939]">
              <p className="text-xs font-semibold text-[#667085] uppercase tracking-wider">
                Up next · {pending.length - currentIdx - 1} movie{pending.length - currentIdx - 1 !== 1 ? 's' : ''}
              </p>
            </div>
            <div className="divide-y divide-[#1D2939] max-h-72 overflow-y-auto">
              {pending.slice(currentIdx + 1, currentIdx + 11).map((movie: Movie, i: number) => (
                <button
                  key={movie.id}
                  onClick={() => setCurrentIdx(currentIdx + 1 + i)}
                  className="w-full flex items-center gap-3 px-4 py-2.5 hover:bg-[#1D2939]/60 transition-colors text-left"
                >
                  <span className="text-xs text-[#475467] w-4 flex-shrink-0">{i + 1}</span>
                  <span className="text-sm text-[#D0D5DD] truncate flex-1">{movie.title}</span>
                  {movie.year && <span className="text-xs text-[#475467] flex-shrink-0">{movie.year}</span>}
                </button>
              ))}
            </div>
          </div>
        )}

        {/* Downloading progress */}
        {downloading && (
          <div className="rounded-xl border border-[#1D2939] bg-[#101828] overflow-hidden">
            <div className="flex items-center gap-2.5 px-4 py-3 border-b border-[#1D2939]">
              <Spinner size={14} className="text-[#BB0000]" />
              <p className="text-sm text-[#D0D5DD]">Downloading theme…</p>
            </div>
            {downloadLogs.length > 0 && (
              <div className="max-h-40 overflow-y-auto px-4 py-3 space-y-0.5">
                {downloadLogs.slice(-20).map((line: string, i: number) => (
                  <p key={i} className="font-mono text-[11px] text-[#475467] leading-relaxed break-all">{line}</p>
                ))}
              </div>
            )}
          </div>
        )}

        {/* Search results */}
        {!downloading && (
          <div className="rounded-xl border border-[#1D2939] bg-[#101828] divide-y divide-[#1D2939]">
            <div className="px-4 py-3 flex items-center gap-2">
              <p className="text-xs font-semibold text-[#667085] uppercase tracking-wider flex-shrink-0">
                YouTube results
              </p>
              {/* Editable search query */}
              <input
                value={searchQuery}
                onChange={(e: { target: { value: string } }) => setSearchQuery(e.target.value)}
                onKeyDown={(e: { key: string }) => { if (e.key === 'Enter' && searchQuery.trim()) reSearch(searchQuery.trim()) }}
                placeholder={`${current.title}${current.year ? ` ${current.year}` : ''} theme`}
                className="flex-1 min-w-0 bg-transparent text-xs text-[#D0D5DD] placeholder:text-[#344054] outline-none"
              />
              {searchQuery.trim() && (
                <button
                  onClick={() => reSearch(searchQuery.trim())}
                  className="flex-shrink-0 text-xs text-[#BB0000] hover:text-[#E07777] transition-colors"
                >
                  Search ↵
                </button>
              )}
              {/* Auto-download best match button */}
              {bestMatch && !searching && (
                <Button size="sm" onClick={doAutoDownload} disabled={downloading}>
                  <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                    <path d="M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2zm0 0v10m0 0-3-3m3 3 3-3" />
                  </svg>
                  Best match
                </Button>
              )}
              {searching && <Spinner size={13} className="text-[#BB0000]" />}
            </div>

            {searching && results.length === 0 && (
              <div className="px-4 py-5 flex items-center gap-2 text-sm text-[#475467]">
                <Spinner size={14} className="text-[#BB0000]" />
                Searching YouTube…
              </div>
            )}

            {!searching && results.length === 0 && !error && (
              <p className="px-4 py-5 text-sm text-[#475467]">No results found.</p>
            )}

            {results.map(r => (
              <div key={r.videoId} className={`flex items-center gap-3 px-4 py-3 transition-colors ${r.bestMatch ? 'bg-[#12B76A]/5 hover:bg-[#12B76A]/10' : 'hover:bg-[#0C111D]/60'}`}>
                {r.thumbnail && (
                  <img
                    src={r.thumbnail}
                    alt={r.title}
                    className="h-12 w-20 flex-shrink-0 rounded object-cover bg-[#1D2939]"
                    loading="lazy"
                  />
                )}
                <div className="flex-1 min-w-0">
                  <div className="flex items-center gap-2 flex-wrap">
                    <p className="text-sm font-medium text-[#F9FAFB] truncate">{r.title}</p>
                    {r.bestMatch && (
                      <span className="flex-shrink-0 text-[10px] font-semibold text-[#12B76A] bg-[#12B76A]/15 px-1.5 py-0.5 rounded">
                        Best match
                      </span>
                    )}
                  </div>
                  <p className="text-xs text-[#667085]">
                    {r.channel}{r.duration ? ` · ${r.duration}` : ''}
                  </p>
                  <a
                    href={`https://www.youtube.com/watch?v=${r.videoId}`}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="text-xs text-[#CC3333] hover:underline"
                  >
                    Preview ↗
                  </a>
                </div>
                <Button
                  size="sm"
                  onClick={() => doDownload(r.videoId)}
                  disabled={downloading}
                >
                  Download
                </Button>
              </div>
            ))}
          </div>
        )}

        {/* Manual URL */}
        {!downloading && (
          <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-4 space-y-3">
            <p className="text-xs font-semibold text-[#667085] uppercase tracking-wider">Paste YouTube URL</p>
            <div className="flex gap-2">
              <Input
                placeholder="https://www.youtube.com/watch?v=…"
                value={manualUrl}
                onChange={e => setManualUrl(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') doDownloadUrl() }}
                className="flex-1"
              />
              <Button
                onClick={doDownloadUrl}
                disabled={!manualUrl.trim()}
                size="md"
              >
                Download
              </Button>
            </div>
          </div>
        )}

        {error && (
          <div className="rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
            <p className="text-sm text-[#FDA29B]">{error}</p>
          </div>
        )}

      </div>
    </AppShell>
  )
}

function MoviePoster({ movie }: { movie: Movie }) {
  const [imgError, setImgError] = useState(false)

  if (movie.posterUrl && !imgError) {
    return (
      <div className="relative h-24 w-16 flex-shrink-0 overflow-hidden rounded-lg bg-[#1D2939]">
        <img
          src={movie.posterUrl}
          alt={movie.title}
          className="absolute inset-0 h-full w-full object-cover"
          onError={() => setImgError(true)}
          loading="lazy"
        />
      </div>
    )
  }

  return (
    <div className="flex h-24 w-16 flex-shrink-0 items-center justify-center rounded-lg bg-[#1D2939]">
      <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="#344054" strokeWidth="1.5" strokeLinecap="round">
        <rect x="2" y="2" width="20" height="20" rx="2" />
        <path d="M7 2v20M17 2v20M2 12h20" />
      </svg>
    </div>
  )
}
