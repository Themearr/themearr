import { useEffect, useRef, useState } from 'react'
import type { MediaItem, MediaStatus } from '@/lib/types'
import type { MediaAdapter } from '@/lib/media/adapter'
import { Button, EmptyState, Spinner } from '@/components/ui'
import { SearchModal } from './SearchModal'

interface MediaGridProps {
  items: MediaItem[]
  adapter: MediaAdapter
  onUpdated: (id: string, status: MediaStatus) => void
  /** Context-dependent empty-state copy — the page knows the source, the grid doesn't. */
  emptyDescription: string
}

type Filter = 'all' | MediaStatus

const STATUS_LABEL: Record<MediaStatus, string> = {
  pending:    'Pending',
  downloaded: 'Downloaded',
  plexTheme:  'Plex theme',
  ignored:    'Ignored',
}

export function MediaGrid({ items, adapter, onUpdated, emptyDescription }: MediaGridProps) {
  const [filter,   setFilter]   = useState<Filter>('all')
  const [search,   setSearch]   = useState('')
  const [selected, setSelected] = useState<MediaItem | null>(null)

  const countOf = (s: MediaStatus) => items.filter(i => i.status === s).length
  const ignored = countOf('ignored')

  const visible = items.filter(i => {
    if (filter !== 'all' && i.status !== filter)    return false
    if (filter === 'all' && i.status === 'ignored') return false
    if (search.trim()) {
      const q = search.toLowerCase()
      return i.title.toLowerCase().includes(q) || String(i.year ?? '').includes(q)
    }
    return true
  })

  // Render the grid in windows so a large library (1000s of movies) never mounts
  // every card at once — that DOM/layout cost, not the data, is what made these
  // pages slow. Grow the window as a bottom sentinel scrolls into view.
  const BATCH = 120
  const [limit, setLimit] = useState(BATCH)
  const sentinelRef = useRef<HTMLDivElement | null>(null)

  // Reset paging whenever the filtered set changes. Done during render (React's
  // "adjusting state when props change" pattern) rather than in an effect, which
  // would trigger an extra cascading render.
  const [pagedFor, setPagedFor] = useState({ filter, search })
  if (pagedFor.filter !== filter || pagedFor.search !== search) {
    setPagedFor({ filter, search })
    setLimit(BATCH)
  }

  const shown   = visible.slice(0, limit)
  const hasMore = limit < visible.length

  useEffect(() => {
    if (!hasMore) return
    const el = sentinelRef.current
    if (!el) return
    const io = new IntersectionObserver(
      entries => { if (entries[0].isIntersecting) setLimit(l => l + BATCH) },
      { rootMargin: '800px' },   // preload before the user reaches the bottom
    )
    io.observe(el)
    return () => io.disconnect()
  }, [hasMore, visible.length])

  return (
    <>
      {/* Toolbar */}
      <div className="mb-5 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="flex items-center gap-1 rounded-lg bg-[#101828] border border-[#1D2939] p-1 flex-wrap">
          {([
            ['all', `All (${items.length - ignored})`],
            // Driven by the adapter, so shows get a Plex theme chip and movies don't.
            // Ignored still only appears once something is ignored.
            ...adapter.statuses
              .filter(s => s !== 'ignored' || ignored > 0)
              .map(s => [s, `${STATUS_LABEL[s]} (${countOf(s)})`] as [Filter, string]),
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

        <div className="relative">
          <svg className="absolute left-3 top-1/2 -translate-y-1/2 text-[#475467]" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <circle cx="11" cy="11" r="8" /><path d="m21 21-4.35-4.35" />
          </svg>
          <input
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder={adapter.labels.searchPlaceholder}
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
          title={search ? `No ${adapter.labels.plural} match your search` : adapter.labels.emptyTitle}
          description={search ? 'Try a different search term' : emptyDescription}
        />
      ) : (
        <>
          <div className="grid grid-cols-3 gap-3 sm:grid-cols-4 md:grid-cols-5 lg:grid-cols-6 xl:grid-cols-8 2xl:grid-cols-10">
            {shown.map(item => (
              <MediaCard
                key={item.id}
                item={item}
                onClick={() => setSelected(item)}
              />
            ))}
          </div>
          {hasMore && (
            <div ref={sentinelRef} className="flex justify-center py-8 text-xs text-[#475467]">
              Loading more… ({shown.length} of {visible.length})
            </div>
          )}
        </>
      )}

      {selected && (
        <MediaActionModal
          item={selected}
          adapter={adapter}
          onClose={() => setSelected(null)}
          onUpdated={(id, status) => { onUpdated(id, status); setSelected(null) }}
        />
      )}
    </>
  )
}

// ── Media action modal ─────────────────────────────────────────────────────────

function MediaActionModal({ item, adapter, onClose, onUpdated }: {
  item: MediaItem
  adapter: MediaAdapter
  onClose: () => void
  onUpdated: (id: string, status: MediaStatus) => void
}) {
  // 'plexTheme' deliberately does NOT auto-open search — the whole point is that Themearr
  // does not fill a show Plex already covers unless the operator asks for it.
  const [view,      setView]      = useState<'default' | 'search'>(item.status === 'pending' ? 'search' : 'default')
  const [replacing, setReplacing] = useState(false)
  const [ignoring,  setIgnoring]  = useState(false)
  const [error,     setError]     = useState('')

  if (view === 'search') {
    return (
      <SearchModal
        item={item}
        adapter={adapter}
        onClose={onClose}
        onDownloaded={id => onUpdated(id, 'downloaded')}
      />
    )
  }

  async function replaceTheme() {
    setReplacing(true)
    setError('')
    try {
      await adapter.deleteTheme(item.id)
      onUpdated(item.id, 'pending')
    } catch (e) {
      setError((e as Error).message)
      setReplacing(false)
    }
  }

  async function unignore() {
    setIgnoring(true)
    try {
      await adapter.unignore(item.id)
      onUpdated(item.id, 'pending')
    } catch (e) {
      setError((e as Error).message)
      setIgnoring(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/60 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-sm rounded-xl border border-[#1D2939] bg-[#101828] shadow-2xl overflow-hidden">
        {/* Header */}
        <div className="flex items-center justify-between border-b border-[#1D2939] px-5 py-4">
          <div className="min-w-0">
            <h2 className="text-sm font-semibold text-[#F9FAFB] truncate">{item.title}</h2>
            {item.year && <p className="text-xs text-[#667085]">{item.year}</p>}
          </div>
          <button onClick={onClose} className="ml-3 flex-shrink-0 text-[#667085] hover:text-[#D0D5DD] transition-colors">
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
              <path d="M18 6 6 18M6 6l12 12" />
            </svg>
          </button>
        </div>

        <div className="p-5 space-y-4">
          {item.status === 'downloaded' && (
            <>
              {/* Audio preview */}
              <div className="space-y-1.5">
                <p className="text-xs font-semibold text-[#667085] uppercase tracking-wider">Theme preview</p>
                <ThemeAudioPreview id={item.id} adapter={adapter} />
              </div>
              <div className="border-t border-[#1D2939]" />
              <Button variant="secondary" size="sm" className="w-full" onClick={() => setView('search')} loading={replacing}>
                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                  <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><polyline points="17 8 12 3 7 8" /><line x1="12" y1="3" x2="12" y2="15" />
                </svg>
                Replace theme
              </Button>
              <Button variant="ghost" size="sm" className="w-full" onClick={replaceTheme} loading={replacing}>
                Delete theme file
              </Button>
            </>
          )}

          {item.status === 'plexTheme' && (
            <div className="space-y-3">
              <p className="text-sm text-[#667085]">
                Plex already has a theme for this show, so Themearr skips it. Downloading one
                writes a <code className="text-[#98A2B3]">theme.mp3</code> into the show folder,
                which takes priority over Plex&apos;s own.
              </p>
              <Button variant="secondary" className="w-full" size="sm" onClick={() => setView('search')}>
                Download anyway
              </Button>
            </div>
          )}

          {item.status === 'ignored' && (
            <div className="space-y-3">
              <p className="text-sm text-[#667085]">
                This {adapter.labels.plural === 'shows' ? 'show' : 'movie'} is ignored and won&apos;t appear in the queue.
              </p>
              <Button className="w-full" size="sm" onClick={unignore} loading={ignoring}>
                Remove from ignore list
              </Button>
            </div>
          )}

          {error && (
            <div className="rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-3 py-2">
              <p className="text-xs text-[#FDA29B]">{error}</p>
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

// ── Media card ─────────────────────────────────────────────────────────────────

function MediaCard({ item, onClick }: { item: MediaItem; onClick: () => void }) {
  const [imgError, setImgError] = useState(false)
  const isPending   = item.status === 'pending'
  const isIgnored   = item.status === 'ignored'
  const isPlexTheme = item.status === 'plexTheme'

  return (
    <button
      onClick={onClick}
      className="group relative flex flex-col text-left focus:outline-none cursor-pointer"
    >
      {/* Poster */}
      <div className={`relative w-full overflow-hidden rounded-lg bg-[#1D2939] ${isIgnored ? 'opacity-40' : ''}`} style={{ aspectRatio: '2/3' }}>
        {item.posterUrl && !imgError ? (
          <img
            src={item.posterUrl}
            alt={item.title}
            className="absolute inset-0 h-full w-full object-cover transition-transform duration-200 group-hover:scale-105"
            onError={() => setImgError(true)}
            loading="lazy"
          />
        ) : (
          <div className="flex h-full w-full flex-col items-center justify-center gap-2 p-2">
            <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="#344054" strokeWidth="1.5" strokeLinecap="round">
              <rect x="2" y="2" width="20" height="20" rx="2" />
              <path d="M7 2v20M17 2v20M2 12h20" />
            </svg>
            <span className="text-center text-[10px] leading-tight text-[#475467] line-clamp-3">{item.title}</span>
          </div>
        )}

        {/* Hover overlay */}
        <div className="absolute inset-0 flex items-center justify-center rounded-lg bg-black/60 opacity-0 transition-opacity duration-200 group-hover:opacity-100">
          <div className={`flex items-center gap-1.5 rounded-md px-3 py-1.5 text-xs font-medium text-white ${isPending ? 'bg-[#BB0000]' : isIgnored ? 'bg-[#344054]' : 'bg-[#1D2939]'}`}>
            {isPending   && <><svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round"><circle cx="11" cy="11" r="8" /><path d="m21 21-4.35-4.35" /></svg>Get theme</>}
            {isIgnored   && <>Ignored</>}
            {isPlexTheme && <>Plex theme</>}
            {!isPending && !isIgnored && !isPlexTheme && <><svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M9 18V5l12-2v13" /><circle cx="6" cy="18" r="3" /><circle cx="18" cy="16" r="3" /></svg>Preview / Replace</>}
          </div>
        </div>

        {/* Downloaded badge */}
        {item.status === 'downloaded' && (
          <div className="absolute bottom-1.5 right-1.5 flex h-5 w-5 items-center justify-center rounded-full bg-[#12B76A]">
            <svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round">
              <path d="M2 6l3 3 5-5" />
            </svg>
          </div>
        )}

        {/* Plex-theme badge. Deliberately NOT the green tick — "Plex has this covered" is
            a different claim from "we fetched this", and they must not read the same. */}
        {isPlexTheme && (
          <div
            title="Plex already has a theme"
            className="absolute bottom-1.5 right-1.5 flex h-5 items-center rounded-full bg-[#344054] px-1.5 text-[9px] font-semibold text-[#D0D5DD]"
          >
            PLEX
          </div>
        )}
      </div>

      {/* Title + year */}
      <div className="mt-1.5 px-0.5">
        <p className={`truncate text-xs font-medium ${isIgnored ? 'text-[#475467]' : 'text-[#D0D5DD]'}`}>{item.title}</p>
        {item.year && <p className="text-[11px] text-[#475467]">{item.year}</p>}
      </div>
    </button>
  )
}

// ── Theme audio preview (fetches via bearer auth, plays from object URL) ─────

function ThemeAudioPreview({ id, adapter }: { id: string; adapter: MediaAdapter }) {
  const [src, setSrc] = useState<string>('')
  const [error, setError] = useState('')

  useEffect(() => {
    let revoked = false
    let objectUrl = ''
    adapter.themeAudioObjectUrl(id)
      .then(url => {
        if (revoked) { URL.revokeObjectURL(url); return }
        objectUrl = url
        setSrc(url)
      })
      .catch(e => setError((e as Error).message))
    return () => {
      revoked = true
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [id, adapter])

  if (error) return <p className="text-xs text-[#FDA29B]">{error}</p>
  if (!src)  return <div className="h-9 flex items-center"><Spinner size={16} /></div>
  return (
    <audio
      controls
      src={src}
      className="w-full h-9"
      style={{ colorScheme: 'dark' }}
    />
  )
}
