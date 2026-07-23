import { act, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'
import type { Movie } from '@/lib/types'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')

function renderPage(ui: React.ReactElement) {
  return render(
    <MemoryRouter>
      <AuthProvider>{ui}</AuthProvider>
    </MemoryRouter>,
  )
}

// Fake timers, because the bug is about the relative timing of the sync poll and
// a slower-than-a-poll-interval /api/movies response. fireEvent + act (not
// userEvent) keeps the two compatible with vitest's fake clock -- see queue-race.
function flush(ms: number) {
  return act(async () => { await vi.advanceTimersByTimeAsync(ms) })
}

type Deferred<T> = { promise: Promise<T>; resolve: (v: T) => void; reject: (e: unknown) => void }
function defer<T>(): Deferred<T> {
  let resolve!: (v: T) => void
  let reject!: (e: unknown) => void
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej })
  return { promise, resolve, reject }
}

const movie = (id: string, title: string): Movie => ({
  id, source: 'plex', sourceRef: `r-${id}`, title, year: 2000,
  sourcePath: null, folderName: title, status: 'pending', posterUrl: null,
} as Movie)

beforeEach(() => {
  vi.clearAllMocks()
  vi.useFakeTimers()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  // Every sync "finishes" on its first status tick, so each sync's post-finish
  // loadMovies fires deterministically one poll interval after the sync starts.
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: true, logs: [] } as never)
  vi.mocked(api.syncApi.start).mockResolvedValue({ started: true } as never)
})

afterEach(() => {
  vi.useRealTimers()
})

describe('a slow movie refresh cannot overwrite a newer one', () => {
  it('keeps the newest sync refresh when an earlier one resolves after it', async () => {
    // Hand-resolved so the test controls exactly which /api/movies response
    // lands and in which order, independent of the order they were issued.
    const listCalls: Deferred<Movie[]>[] = []
    vi.mocked(api.moviesApi.list).mockImplementation(() => {
      const d = defer<Movie[]>()
      listCalls.push(d)
      return d.promise as never
    })

    const { default: MoviesPage } = await import('@/app/movies/page')
    renderPage(<MoviesPage />)

    // call 0: the initial load. Non-empty, so the (deliberately silent)
    // empty-library auto-sync path never fires.
    await act(async () => { listCalls[0].resolve([movie('a', 'Movie A'), movie('b', 'Movie B')]) })
    await flush(0)

    // Sync #1 -> its poll finishes one interval later and issues refresh R (call 1).
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /sync/i })) })
    await flush(1500)
    expect(listCalls).toHaveLength(2)

    // Sync #2 -> its poll issues refresh P (call 2), strictly AFTER R was issued.
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /sync/i })) })
    await flush(1500)
    expect(listCalls).toHaveLength(3)

    // The newer request P settles first, with the fresh list that includes Movie D...
    await act(async () => { listCalls[2].resolve([movie('a', 'Movie A'), movie('b', 'Movie B'), movie('d', 'Movie D')]) })
    await flush(0)
    // ...then the older R settles last, carrying a list from before Movie D existed.
    await act(async () => { listCalls[1].resolve([movie('a', 'Movie A'), movie('b', 'Movie B')]) })
    await flush(0)

    // The last-issued refresh must win even though it settled first: Movie D stays.
    expect(screen.queryAllByText('Movie D').length).toBeGreaterThan(0)
  })
})
