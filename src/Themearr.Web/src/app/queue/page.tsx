'use client'

import Image from 'next/image'
import { useEffect, useRef, useState } from 'react'
import { moviesApi, settingsApi } from '@/lib/api'
import type { Movie, YoutubeResult } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { Button, Input, Spinner } from '@/components/ui'

export default function QueuePage() {
  const [pending,      setPending]      = useState<Movie[] | null>(null)
  const [currentIdx,   setCurrentIdx]   = useState(0)
  const [results,      setResults]      = useState<YoutubeResult[]>([])
  const [searching,    setSearching]    = useState(false)
  const [searchQuery,  setSearchQuery]  = useState('')
  const [manualUrl,    setManualUrl]    = useState('')
  const [error,        setError]        = useState('')
  const [downloading,  setDownloading]  = useState(false)
  const [autoMode,     setAutoMode]     = useState(false)
  const [upNextOpen,   setUpNextOpen]   = useState(false)

  // Holds the movieId being downloaded so the polling closure keeps the right id
  const downloadingMovieId = useRef<string | null>(null)
  const searchedFor        = useRef<string | null>(null)
  // Tracks whether we've already triggered auto-download for the current movie
  const autoTriggeredFor   = useRef<string | null>(null)

  const current   = pending?.[currentIdx] ?? null
  const remaining = pending ? Math.max(0, pending.length - currentIdx) : 0

  // ── Load pending movies + auto mode setting ────────────────────────────────
  useEffect(() => {
    moviesApi.list()
      .then(movies => setPending(movies.filter(m => m.status === 'pending')))
      .catch(() => setPending([]))
    settingsApi.get()
      .then(s => setAutoMode(s.autoDownload))
      .catch(() => null)
  }, [])

  async function toggleAutoMode() {
    const next = !autoMode
    setAutoMode(next)
    try {
      const s = await settingsApi.get()
      await settingsApi.save({ ...s, autoDownload: next })
    } catch { /* ignore */ }
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

  async function skipForever() {
    if (!current) return
    try { await moviesApi.ignoreMovie(current.id) } catch { /* ignore */ }
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

    const id = setInterval(async () => {
      try {
        const st = await moviesApi.downloadStatus(movieId)
        if (!st.finished) return
        clearInterval(id)
        if (st.error) {
          setError(st.error)
          setDownloading(false)
        } else {
          advanceQueue()
        }
      } catch { /* ignore transient fetch errors */ }
    }, 1000)

    return () => clearInterval(id)
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [downloading])

  function advanceQueue() {
    setCurrentIdx((i: number) => i + 1)
    setResults([])
    setError('')
    setManualUrl('')
    setDownloading(false)
    downloadingMovieId.current = null
  }

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

  // ── Loading ────────────────────────────────────────────────────────────────
  if (pending === null) {
    return (
      <AppShell title="Queue">
        <div className="flex justify-center py-24">
          <Spinner size={28} className="text-[#BB0000]" />
        </div>
      </AppShell>
    )
  }

  // ── All done ───────────────────────────────────────────────────────────────
  if (!current) {
    return (
      <AppShell title="Queue">
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
            className={`flex items-center gap-2 rounded-lg px-2.5 py-1.5 text-xs font-medium transition-colors ${autoMode ? 'bg-[#12B76A]/15 text-[#12B76A]' : 'bg-[#1D2939] text-[#667085] hover:text-[#D0D5DD]'}`}
          >
            <span className={`relative inline-flex h-4 w-7 flex-shrink-0 rounded-full border-2 border-transparent transition-colors ${autoMode ? 'bg-[#12B76A]' : 'bg-[#344054]'}`}>
              <span className={`inline-block h-3 w-3 transform rounded-full bg-white shadow transition-transform ${autoMode ? 'translate-x-3' : 'translate-x-0'}`} />
            </span>
            Auto
          </button>
          <Button variant="ghost" size="sm" onClick={skipForever} disabled={downloading} title="Never show this movie in the queue again">
            Ignore
          </Button>
          <Button variant="ghost" size="sm" onClick={advanceQueue} disabled={downloading}>
            Skip
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
              <path d="M5 12h14M12 5l7 7-7 7" />
            </svg>
          </Button>
        </div>
      }
    >
      <div className="max-w-2xl space-y-5">

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

        {/* Up next collapsible */}
        {pending && pending.length > currentIdx + 1 && (
          <div className="rounded-xl border border-[#1D2939] bg-[#101828] overflow-hidden">
            <button
              className="w-full flex items-center justify-between px-4 py-3 text-xs font-semibold text-[#667085] uppercase tracking-wider hover:bg-[#1D2939]/50 transition-colors"
              onClick={() => setUpNextOpen((o: boolean) => !o)}
            >
              <span>Up next · {pending.length - currentIdx - 1} movie{pending.length - currentIdx - 1 !== 1 ? 's' : ''}</span>
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className={`transition-transform ${upNextOpen ? 'rotate-180' : ''}`}>
                <path d="M6 9l6 6 6-6" />
              </svg>
            </button>
            {upNextOpen && (
              <div className="divide-y divide-[#1D2939] border-t border-[#1D2939] max-h-64 overflow-y-auto">
                {pending.slice(currentIdx + 1, currentIdx + 11).map((movie: Movie, i: number) => (
                  <button
                    key={movie.id}
                    onClick={() => { setCurrentIdx(currentIdx + 1 + i); setUpNextOpen(false) }}
                    className="w-full flex items-center gap-3 px-4 py-2.5 hover:bg-[#1D2939]/60 transition-colors text-left"
                  >
                    <span className="text-xs text-[#475467] w-4 flex-shrink-0">{i + 1}</span>
                    <span className="text-sm text-[#D0D5DD] truncate flex-1">{movie.title}</span>
                    {movie.year && <span className="text-xs text-[#475467] flex-shrink-0">{movie.year}</span>}
                  </button>
                ))}
              </div>
            )}
          </div>
        )}

        {/* Downloading progress */}
        {downloading && (
          <div className="flex items-center gap-2.5 rounded-xl border border-[#1D2939] bg-[#101828] px-4 py-3">
            <Spinner size={14} className="text-[#BB0000]" />
            <p className="text-sm text-[#D0D5DD]">Downloading theme…</p>
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
        <Image
          src={movie.posterUrl}
          alt={movie.title}
          fill
          sizes="64px"
          className="object-cover"
          onError={() => setImgError(true)}
          unoptimized
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
