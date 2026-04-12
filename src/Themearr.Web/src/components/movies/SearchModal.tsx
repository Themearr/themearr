'use client'

import { useState } from 'react'
import { moviesApi } from '@/lib/api'
import type { Movie, YoutubeResult } from '@/lib/types'
import { Button, Modal, Spinner, Input } from '@/components/ui'

interface SearchModalProps {
  movie: Movie
  onClose: () => void
  onDownloaded: (movieId: string) => void
}

export function SearchModal({ movie, onClose, onDownloaded }: SearchModalProps) {
  const [results, setResults] = useState<YoutubeResult[]>([])
  const [searching, setSearching] = useState(false)
  const [downloading, setDownloading] = useState<string | null>(null)
  const [manualUrl, setManualUrl] = useState('')
  const [error, setError] = useState('')
  const [searched, setSearched] = useState(false)

  async function doSearch() {
    setSearching(true)
    setError('')
    try {
      const data = await moviesApi.search(movie.id)
      setResults(data.results)
      setSearched(true)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setSearching(false)
    }
  }

  async function doDownload(videoId: string) {
    setDownloading(videoId)
    setError('')
    try {
      await moviesApi.download(movie.id, videoId)
      onDownloaded(movie.id)
      onClose()
    } catch (e) {
      setError((e as Error).message)
      setDownloading(null)
    }
  }

  async function doDownloadUrl() {
    if (!manualUrl.trim()) return
    setDownloading('url')
    setError('')
    try {
      await moviesApi.downloadUrl(movie.id, manualUrl.trim())
      onDownloaded(movie.id)
      onClose()
    } catch (e) {
      setError((e as Error).message)
      setDownloading(null)
    }
  }

  return (
    <Modal open onClose={onClose} title={`${movie.title} (${movie.year ?? '?'})`} size="lg">
      <div className="space-y-5">
        {/* Search button */}
        {!searched && (
          <Button onClick={doSearch} loading={searching} className="w-full">
            {searching ? 'Searching YouTube…' : 'Search YouTube for theme'}
          </Button>
        )}

        {/* Results */}
        {results.length > 0 && (
          <div className="space-y-2">
            <p className="text-xs font-medium text-[#667085] uppercase tracking-wider">Results</p>
            {results.map(r => (
              <div
                key={r.videoId}
                className="flex items-center gap-3 rounded-lg border border-[#1D2939] bg-[#0C111D] p-3 hover:border-[#344054] transition-colors"
              >
                {r.thumbnail && (
                  <img
                    src={r.thumbnail}
                    alt={r.title}
                    className="h-14 w-24 flex-shrink-0 rounded object-cover bg-[#1D2939]"
                    loading="lazy"
                  />
                )}
                <div className="flex-1 min-w-0">
                  <p className="text-sm font-medium text-[#F9FAFB] truncate">{r.title}</p>
                  <p className="text-xs text-[#667085]">
                    {r.channel}{r.duration ? ` · ${r.duration}` : ''}
                  </p>
                  {/* Embedded preview */}
                  <a
                    href={`https://www.youtube.com/watch?v=${r.videoId}`}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="text-xs text-[#9E77ED] hover:underline"
                  >
                    Preview ↗
                  </a>
                </div>
                <Button
                  size="sm"
                  onClick={() => doDownload(r.videoId)}
                  loading={downloading === r.videoId}
                  disabled={downloading !== null}
                >
                  Download
                </Button>
              </div>
            ))}
          </div>
        )}

        {/* Re-search after viewing results */}
        {searched && (
          <Button variant="ghost" size="sm" onClick={doSearch} loading={searching}>
            Search again
          </Button>
        )}

        {/* Manual URL */}
        <div className="border-t border-[#1D2939] pt-4 space-y-3">
          <p className="text-xs font-medium text-[#667085] uppercase tracking-wider">Paste YouTube URL</p>
          <div className="flex gap-2">
            <Input
              placeholder="https://www.youtube.com/watch?v=…"
              value={manualUrl}
              onChange={e => setManualUrl(e.target.value)}
              onKeyDown={e => e.key === 'Enter' && doDownloadUrl()}
              className="flex-1"
            />
            <Button
              onClick={doDownloadUrl}
              loading={downloading === 'url'}
              disabled={!manualUrl.trim() || downloading !== null}
              size="md"
            >
              Download
            </Button>
          </div>
        </div>

        {error && (
          <div className="rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
            <p className="text-sm text-[#FDA29B]">{error}</p>
          </div>
        )}
      </div>
    </Modal>
  )
}
