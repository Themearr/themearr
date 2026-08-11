import { useCallback, useEffect, useRef, useState } from 'react'
import { settingsApi } from '@/lib/api'
import type { MediaItem, YoutubeResult } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { Button, EmptyState, ErrorIcon, Input, Spinner } from '@/components/ui'
import { useResource } from '@/lib/useResource'
import { moviesAdapter, showsAdapter, type MediaAdapter } from '@/lib/media/adapter'

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
  // Component state, deliberately not persisted and not in the URL: someone who once
  // looked at shows would otherwise return to the Queue, find an empty triage list and
  // conclude it is broken. Movies are the default library and the safe default view.
  const [media, setMedia] = useState<'movies' | 'shows'>('movies')
  const adapter = media === 'movies' ? moviesAdapter : showsAdapter

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
  // In-flight guard for Ignore (same reasoning as savingAuto): the round trip
  // leaves the button live otherwise, and a double click means two ignores and
  // two advances -- the second one silently dropping the NEXT item from triage.
  const [ignoring,      setIgnoring]      = useState(false)

  // Holds the movieId being downloaded so the polling closure keeps the right id
  const downloadingMovieId = useRef<string | null>(null)
  // Monotonic download-attempt stamp -- the ownership half of settleDownloadFailure's
  // identity gate. The bare id is not enough there: after the status poll's
  // lost-contact path hands control back, a *second* download of the same item
  // (or of a show sharing the id) can be running when the first attempt's hung
  // request finally rejects, and an id comparison would let that stale rejection
  // tear down the newer attempt's tracking. Same latest-stamp technique
  // useResource (src/lib/useResource.ts) uses: each starter claims a number at
  // issue time, and only the newest issued may settle the shared download state.
  const downloadAttempt    = useRef(0)
  // Identifies the newest search, so a slow earlier one cannot land its
  // results, error, or spinner-clear under whichever item is on screen when it
  // settles -- the latest-stamp technique again (useResource, downloadAttempt).
  // Stale results are worse than a stale banner: they are clickable, and
  // Download pairs the on-screen item's id with the stale result's videoId --
  // the previous item's theme, downloaded onto this one.
  const searchSeq          = useRef(0)
  // What the running download is bound to, captured at start alongside the
  // attempt stamp: the adapter and media it was started under, plus the
  // on-screen key its errors belong to. The status poll reads THIS rather than
  // the live `adapter`/`media` -- the toggle stays clickable during a download
  // (#43), so by the next tick the live values may describe the other library,
  // and asking the shows endpoint about a movie's download answers "no such
  // job, not finished" forever (DownloadService.GetStatus's unknown-id shape),
  // wedging the queue in "Downloading…" with Skip/Ignore disabled.
  const downloadBinding    = useRef<{ adapter: MediaAdapter; media: 'movies' | 'shows'; attempt: number; key: string } | null>(null)
  const searchedFor        = useRef<string | null>(null)
  // Tracks whether we've already triggered auto-download for the current movie
  const autoTriggeredFor   = useRef<string | null>(null)
  // Keep a ref in sync with autoMode so polling closures never see stale state
  const autoModeRef        = useRef(autoMode)
  // Same, for the media toggle: the poll's settle paths need to know which
  // library is on screen *now*, not which one their download started under.
  const mediaRef           = useRef(media)

  // Which media the list in `movies` belongs to. useResource keeps the old
  // data until a refetch lands, so after switchMedia there is a beat where
  // `current` is still the OTHER library's item while `media`/`adapter` are
  // already the new one -- a torn pair that must not be rendered as a queue or
  // acted on by the effects below (ids come from different tables, so a
  // shows-adapter call with a movie's id hits a nonexistent -- or worse, a
  // colliding -- show). Tagged in the fetcher, gated by its own latest-stamp
  // so an abandoned fetch settling late cannot mislabel a newer list.
  const [listFor, setListFor] = useState<'movies' | 'shows'>('movies')
  const listFetchSeq = useRef(0)
  // The initial load. Routed through useResource so a failed request surfaces
  // as an error screen instead of "every movie already has a theme".
  const { data: movies, error: moviesError, retry: retryMovies } = useResource(useCallback(() => {
    const mine = ++listFetchSeq.current
    return adapter.list().then(list => {
      if (mine === listFetchSeq.current) setListFor(media)
      return list
    })
  }, [adapter, media]))
  // 'pending' only — a plexTheme show is not outstanding work, which matches
  // GetPendingShows filtering on plex_has_theme = 0. Manual triage and the
  // auto-download worker therefore agree on what is left to do.
  const pending = movies ? movies.filter(m => m.status === 'pending') : null

  // useResource only refetches when retry() bumps its attempt counter — handing it a new
  // fetcher identity is not enough — so switching media has to ask for the reload
  // explicitly, and reset everything keyed to the old list.
  function switchMedia(next: 'movies' | 'shows') {
    if (next === media) return
    setMedia(next)
    // Assigned here as well as in the sync effect below: that effect is
    // passive, so a poll tick due in the commit-to-effect gap would still read
    // the old media and could act on the wrong list's index.
    mediaRef.current = next
    setCurrentIdx(0)
    setResults([])
    setError('')
    setManualUrl('')
    searchedFor.current      = null
    autoTriggeredFor.current = null
    // Invalidates any in-flight search: its results belong to the library the
    // user just left, and the new library's search claims a fresh stamp.
    searchSeq.current++
    retryMovies()
  }

  const current   = pending?.[currentIdx] ?? null
  const remaining = pending ? Math.max(0, pending.length - currentIdx) : 0

  // What the card is showing right now, for the download catch handlers below:
  // the "Up next" rows and the media toggle deliberately stay clickable while a
  // download runs, so by the time a request rejects the card may be showing a
  // different item -- and the rejection's error must not render under it (#43).
  // A ref (same technique as autoModeRef) because the deciders are closures
  // that outlive the render they were created in. Media-qualified because
  // movie and show ids come from different tables and can collide -- a bare id
  // can't tell movie 7 from show 7 across the toggle.
  const onScreenKeyRef = useRef<string | null>(null)
  useEffect(() => { onScreenKeyRef.current = current ? `${media}:${current.id}` : null }, [current, media])

  // ── Load auto mode setting ──────────────────────────────────────────────────
  useEffect(() => {
    settingsApi.get()
      .then(s => setAutoMode(s.autoDownload))
      .catch(() => null)
  }, [])

  // Keep refs in sync
  useEffect(() => { autoModeRef.current = autoMode }, [autoMode])
  useEffect(() => { mediaRef.current = media }, [media])

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
    // Torn beat (see listFor above): searching the other library for this id
    // would be a request for an item that doesn't exist there. Returning
    // without setting searchedFor keeps the real search armed for when the
    // right list lands.
    if (listFor !== media) return
    if (!current || searchedFor.current === current.id) return
    searchedFor.current = current.id
    const mine = ++searchSeq.current
    setResults([])
    setError('')
    setManualUrl('')
    setSearchQuery('')
    setSearching(true)
    adapter.search(current.id)
      .then(data => { if (mine === searchSeq.current) setResults(data.results) })
      .catch((e: Error) => { if (mine === searchSeq.current) setError(e.message) })
      .finally(() => { if (mine === searchSeq.current) setSearching(false) })
  }, [current, adapter, listFor, media])

  function reSearch(q?: string) {
    if (!current) return
    const mine = ++searchSeq.current
    setResults([])
    setError('')
    setSearching(true)
    adapter.search(current.id, q || undefined)
      .then(data => { if (mine === searchSeq.current) setResults(data.results) })
      .catch((e: Error) => { if (mine === searchSeq.current) setError(e.message) })
      .finally(() => { if (mine === searchSeq.current) setSearching(false) })
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
  // no theme, no error and no trace) is completely silent. Skip passes nothing
  // and always advances -- it acts on the user's immediate click. Ignore also
  // passes nothing, but only calls this after its own check that the ignored
  // item is still on screen (see skipForever).
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

  // Every download failure lands here -- the four starters' catches with the
  // attempt stamp and on-screen key they captured before their request went
  // out, and the status poll's settle paths with the ones captured at download
  // start (a server-reported failure is the same event arriving on the other
  // channel). Two identity checks, in order:
  //
  // 1. Ownership: only the newest attempt may clear the in-flight state. The
  //    status poll's lost-contact path can hand control back mid-request, so a
  //    newer download -- possibly of the same item -- may be running by the
  //    time this rejection finally arrives, and clearing `downloading` then
  //    would kill the newer attempt's tracking.
  // 2. Placement: the error only renders if the item it concerns is still the
  //    one on the card. A stale banner under whatever the user browsed to
  //    reads as *that* title having failed (#43); the failed item is pending
  //    either way, so the queue will offer it again.
  function settleDownloadFailure(attempt: number, forKey: string, message: string) {
    if (downloadAttempt.current !== attempt) return
    setDownloading(false)
    downloadingMovieId.current = null
    if (onScreenKeyRef.current === forKey) setError(message)
  }

  // The ignore round-trip has the same shape as a download request: the queue
  // stays browsable while it's in flight, so its outcome must pass the #43
  // placement check before acting on whatever is on screen by then.
  async function skipForever() {
    if (!current || ignoring) return
    const forKey = `${media}:${current.id}`
    setIgnoring(true)
    try {
      await adapter.ignore(current.id)
    } catch (e) {
      // The server didn't record the ignore, so advancing would hide a movie it
      // still has as pending -- it'd reappear on the next load, making the button
      // look like it did nothing. Surface the failure -- under the item it
      // belongs to only -- and don't advance.
      if (onScreenKeyRef.current === forKey) setError((e as Error).message)
      return
    } finally {
      setIgnoring(false)
    }
    // Advance only past the item that was ignored: if the user browsed on while
    // the request ran, the +1 would push them off an item that was never
    // triaged. The ignore is recorded server-side either way; the item leaves
    // the list on the next load.
    if (onScreenKeyRef.current === forKey) advanceQueue()
  }

  // ── Auto-download in auto mode ─────────────────────────────────────────────
  // Calls the server-side auto-download endpoint directly rather than waiting
  // for client-side search results — avoids silent failures from scoring edge cases.
  useEffect(() => {
    if (!autoMode || !current || downloading) return
    // The switch's torn beat: `current` still belongs to the other library
    // (see listFor above), so "auto-download the shown item" would send an
    // old-media id through the new media's endpoints.
    if (listFor !== media) return
    if (autoTriggeredFor.current === current.id) return

    const forId   = current.id
    const forKey  = `${media}:${forId}`
    const attempt = ++downloadAttempt.current
    autoTriggeredFor.current = forId
    downloadingMovieId.current = forId
    downloadBinding.current = { adapter, media, attempt, key: forKey }
    setDownloading(true)
    setError('')
    adapter.autoDownload(forId)
      .catch((e: Error) => {
        settleDownloadFailure(attempt, forKey, e.message)
        // Deliberately NOT resetting autoTriggeredFor here. `downloading` flipping back
        // to false re-runs this effect, and a cleared guard reads as "never tried this
        // one" -- so a movie that fails once would retry at round-trip rate forever, each
        // attempt wiping the previous error via the setError('') above. Leaving the guard
        // set makes "already tried this one" durable across the re-check: the failure
        // stays visible, and Skip/Ignore plus the per-result Download button remain the
        // way out.
      })
  }, [autoMode, current, downloading, adapter, media, listFor])

  // ── Poll download status while a download is in flight ────────────────────
  useEffect(() => {
    if (!downloading) return
    const movieId = downloadingMovieId.current
    if (!movieId) return
    // Bound at download start, deliberately NOT the live `adapter` (which is
    // why it isn't a dependency): a mid-download media toggle must not re-point
    // this poll at the other library's status endpoint -- see downloadBinding's
    // comment for what that wedges. The attempt stamp and key ride along so the
    // settle paths below can run the same identity gate as the catch sites.
    const binding = downloadBinding.current
    if (!binding) return
    const { adapter: boundAdapter, media: forMedia, attempt, key: forKey } = binding

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
        const st = await boundAdapter.downloadStatus(movieId, { signal: controller.signal })
        failures = 0 // a status came back -- we're still in contact with the server
        // Ownership-gated like every settle below: a stale tick surviving a
        // handback must not paint the old attempt's logs under a newer one.
        if (st.logs?.length && downloadAttempt.current === attempt) setDownloadLogs(st.logs)
        if (!st.finished) return
        clearInterval(id)
        if (st.error) {
          // The server-reported twin of the catch sites' rejections (#43):
          // same stakes, same gate. Without it, browsing away mid-download
          // painted this error under whichever card the user was on.
          settleDownloadFailure(attempt, forKey, st.error)
          // Auto mode: skip this movie automatically after a short pause
          if (autoModeRef.current) {
            setTimeout(() => {
              // Re-checked at fire time: three seconds is long enough for auto
              // mode to have started the next item's download (whose freshly
              // shown error this would wipe) or for the user to be triaging
              // the other library (whose queue this would advance).
              if (downloadAttempt.current !== attempt) return
              if (mediaRef.current !== forMedia) return
              setError('')
              advanceQueue()
            }, 3000)
          }
        } else if (downloadAttempt.current === attempt && onScreenKeyRef.current === forKey) {
          advanceQueue(movieId)
        } else if (downloadAttempt.current === attempt) {
          // Finished, but the downloaded item is no longer the card: advancing
          // would silently skip whatever IS -- the historical queue-race bug
          // class. The id gate inside advanceQueue can't catch this on its own,
          // because a switch-away-and-back re-bases currentIdx onto a refetched
          // list that may already exclude the finished item, leaving the +1 to
          // land on the new head. So: end the tracking here and let list
          // reloads converge -- the completed item stops being pending on the
          // next load, which switching back already triggers
          // (switchMedia -> retryMovies).
          setDownloading(false)
          setDownloadLogs([])
          downloadingMovieId.current = null
        }
      } catch {
        // A transient drop/timeout: the next tick normally recovers it. But if
        // enough fail in a row the server is unreachable, and staying in the
        // "Downloading…" state would wedge the queue with Skip/Ignore disabled.
        // Give up tracking, say so, and re-enable the in-app escapes. We can't
        // know the download's real outcome, so we don't advance -- a reload (or
        // the next visit) will reflect whatever actually happened server-side.
        // Routed through the identity gate: a hung check can settle long after
        // control was handed back, when a newer attempt may own the state and
        // a different item may own the card.
        failures++
        if (failures >= STATUS_MAX_FAILURES) {
          clearInterval(id)
          settleDownloadFailure(attempt, forKey,
            'Lost contact with the server while tracking this download. It may still finish — reload to check.')
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
    // The buttons that reach the manual starters are hidden or disabled while
    // a download runs, so this cannot fire today -- but a second concurrent
    // start would corrupt every invariant the poll relies on (the old
    // effect's interval would keep running against a reassigned binding), so
    // the guard is structural, not decorative.
    if (downloading) return
    if (!current) return
    const forId   = current.id
    const forKey  = `${media}:${forId}`
    const attempt = ++downloadAttempt.current
    downloadingMovieId.current = forId
    downloadBinding.current = { adapter, media, attempt, key: forKey }
    setDownloading(true)
    setError('')
    try {
      await adapter.download(forId, videoId)
    } catch (e) {
      settleDownloadFailure(attempt, forKey, (e as Error).message)
    }
  }

  async function doDownloadUrl() {
    if (downloading) return // structural, as in doDownload
    if (!current || !manualUrl.trim()) return
    const forId   = current.id
    const forKey  = `${media}:${forId}`
    const attempt = ++downloadAttempt.current
    downloadingMovieId.current = forId
    downloadBinding.current = { adapter, media, attempt, key: forKey }
    setDownloading(true)
    setError('')
    try {
      await adapter.downloadUrl(forId, manualUrl.trim())
    } catch (e) {
      settleDownloadFailure(attempt, forKey, (e as Error).message)
    }
  }

  async function doAutoDownload() {
    if (downloading) return // structural, as in doDownload
    if (!current) return
    const forId   = current.id
    const forKey  = `${media}:${forId}`
    const attempt = ++downloadAttempt.current
    downloadingMovieId.current = forId
    downloadBinding.current = { adapter, media, attempt, key: forKey }
    setDownloading(true)
    setError('')
    try {
      await adapter.autoDownload(forId)
    } catch (e) {
      settleDownloadFailure(attempt, forKey, (e as Error).message)
    }
  }

  // Rendered on every return path, not just the populated one: with an empty movie queue
  // the page shows "All caught up!", and without the toggle there would be no way to
  // reach the show queue from there at all.
  const mediaToggle = (
    <div className="mb-5 flex w-fit items-center gap-1 rounded-lg border border-[#1D2939] bg-[#101828] p-1">
      {/* Labels are real text, not CSS `capitalize` — the accessible name is what screen
          readers announce, and a text-transform doesn't change it. */}
      {([['movies', 'Movies'], ['shows', 'Shows']] as const).map(([value, label]) => (
        <button
          key={value}
          onClick={() => switchMedia(value)}
          className={`rounded-md px-3 py-1.5 text-xs font-medium transition-all
            ${media === value ? 'bg-[#1D2939] text-[#F9FAFB] shadow-sm' : 'text-[#667085] hover:text-[#D0D5DD]'}`}
        >
          {label}
        </button>
      ))}
    </div>
  )

  // ── Loading / failed ───────────────────────────────────────────────────────
  // The listFor mismatch is the switch's torn beat (see listFor above): the
  // only truthful render is "loading" -- showing the old library's items under
  // the new toggle invites acting on them across media.
  if (pending === null || listFor !== media) {
    return (
      <AppShell title="Queue">
        {mediaToggle}
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
        {mediaToggle}
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
          <p className="text-sm text-[#667085]">
            Every {adapter.labels.singular} in your library has a theme.
          </p>
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
          <Button variant="ghost" size="sm" onClick={skipForever} disabled={downloading || ignoring} title={`Never show this ${adapter.labels.singular} in the queue again`}>
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
        {mediaToggle}

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
              {remaining} {adapter.labels.singular}{remaining !== 1 ? 's' : ''} left in queue
            </p>
          </div>
        </div>

        {/* Up next */}
        {pending && pending.length > currentIdx + 1 && (
          <div className="rounded-xl border border-[#1D2939] bg-[#101828] overflow-hidden">
            <div className="px-4 py-3 border-b border-[#1D2939]">
              <p className="text-xs font-semibold text-[#667085] uppercase tracking-wider">
                Up next · {pending.length - currentIdx - 1} {adapter.labels.singular}{pending.length - currentIdx - 1 !== 1 ? 's' : ''}
              </p>
            </div>
            <div className="divide-y divide-[#1D2939] max-h-72 overflow-y-auto">
              {pending.slice(currentIdx + 1, currentIdx + 11).map((movie: MediaItem, i: number) => (
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

function MoviePoster({ movie }: { movie: MediaItem }) {
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
