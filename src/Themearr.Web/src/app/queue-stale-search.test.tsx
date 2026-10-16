import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')
const QueuePage = (await import('@/app/queue/page')).default

// The pages render inside AppShell, which guards on useAuth() (loading/authorized)
// before rendering children at all, so the wrapper needs the auth context as well
// as a router.
function renderPage() {
  return render(
    <MemoryRouter>
      <AuthProvider>
        <QueuePage />
      </AuthProvider>
    </MemoryRouter>,
  )
}

// A search response the test settles by hand: the bug is entirely about a slow
// search settling after the user browsed on, so it must be held in flight while
// the test navigates -- the same deferred technique as queue-stale-error.
function deferredSearch() {
  let resolve!: (v: unknown) => void
  let reject!: (e: Error) => void
  const promise = new Promise((res, rej) => { resolve = res; reject = rej })
  return { promise, resolve, reject }
}

const movie = (id: string, title: string, year: number) => ({
  id, source: 'plex', sourceRef: `r-${id}`, title, year,
  sourcePath: null, folderName: title, status: 'pending', posterUrl: null,
})

const result = (videoId: string, title: string) => ({
  videoId, title, thumbnail: null, duration: null, channel: 'ch', score: 1, bestMatch: false,
})

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)

  vi.mocked(api.moviesApi.list).mockResolvedValue([
    movie('a', 'Movie A', 2001),
    movie('b', 'Movie B', 2002),
    movie('c', 'Movie C', 2003),
  ] as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false } as never)
})

// Search responses carry no identity of their own, and the queue auto-searches
// every item the card lands on -- so a slow search can settle with a different
// item on screen. Its results (or error) landing there is #43's bug through the
// search channel, with a sharper edge: a stale result list is clickable, and
// Download sends the on-screen item's id with the stale result's videoId --
// downloading the previous item's theme onto this one.
describe('a stale search cannot land under the item the user browsed to', () => {
  it("a slow search's results do not replace the results of the movie now on screen", async () => {
    const user = userEvent.setup()
    const slow = deferredSearch()
    vi.mocked(api.moviesApi.search).mockImplementation(((id: string) =>
      id === 'a' ? slow.promise : Promise.resolve({ movie: {}, results: [result('vb', 'B theme')] })) as never)

    renderPage()
    await screen.findByText('3 movies left in queue')
    // Movie A's auto-search is hung; the user browses to Movie B, whose search
    // returns immediately.
    await user.click(screen.getByRole('button', { name: /movie b/i }))
    await screen.findByText('B theme')

    // A's search finally answers, with B on the card.
    await act(async () => { slow.resolve({ movie: {}, results: [result('va', 'A theme')] }) })

    expect(screen.queryByText('A theme')).toBeNull()
    expect(screen.queryByText('B theme')).not.toBeNull()
  })

  it("a slow search's failure does not put its error under the movie now on screen", async () => {
    const user = userEvent.setup()
    const slow = deferredSearch()
    vi.mocked(api.moviesApi.search).mockImplementation(((id: string) =>
      id === 'a' ? slow.promise : Promise.resolve({ movie: {}, results: [result('vb', 'B theme')] })) as never)

    renderPage()
    await screen.findByText('3 movies left in queue')
    await user.click(screen.getByRole('button', { name: /movie b/i }))
    await screen.findByText('B theme')

    await act(async () => { slow.reject(new Error('search exploded on Movie A')) })

    expect(screen.queryByText(/search exploded on Movie A/i)).toBeNull()
    // B's own results are untouched.
    expect(screen.queryByText('B theme')).not.toBeNull()
  })
})
