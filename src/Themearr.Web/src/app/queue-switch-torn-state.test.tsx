import { act, render, screen, waitFor } from '@testing-library/react'
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

// The shows list is held in flight by hand: the torn window under test is
// exactly the beat between clicking the Shows toggle and the shows list
// arriving, while `useResource` still holds the movie list.
function deferredList() {
  let resolve!: (v: unknown) => void
  const promise = new Promise(res => { resolve = res })
  return { promise, resolve }
}

const item = (id: string, title: string, year: number) => ({
  id, source: 'plex', sourceRef: `r-${id}`, title, year,
  sourcePath: null, folderName: title, status: 'pending', posterUrl: null,
})

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)

  vi.mocked(api.moviesApi.list).mockResolvedValue([item('a', 'Movie A', 2001)] as never)
  vi.mocked(api.moviesApi.search).mockResolvedValue({ movie: {}, results: [] } as never)
})

// switchMedia swaps `media` (and with it the adapter) and clears the per-item
// guards immediately, but the list from useResource stays the OLD media's until
// the refetch lands. During that beat `current` is an old-media item under the
// new media's adapter -- and any effect that acts on the pair acts across media:
// ids come from different tables, so shows-adapter calls with a movie's id hit
// a nonexistent show, or with colliding ids a real show the user never queued.
describe('the movies->shows switch cannot act on a torn current/adapter pair', () => {
  it('auto mode does not auto-download the old movie through the shows adapter', async () => {
    const user = userEvent.setup()
    // Auto mode is on; Movie A's auto-download fails at mount, which settles
    // `downloading` and leaves auto mode idle -- the state a switch starts from.
    vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: true } as never)
    vi.mocked(api.moviesApi.autoDownload).mockRejectedValue(
      new Error('No suitable match found — please select manually.'),
    )
    // Any shows-side search finds a best match, so a torn auto-download would
    // proceed all the way to a real download call.
    vi.mocked(api.showsApi.search).mockResolvedValue({
      results: [{ videoId: 'v-best', title: 'Best', thumbnail: null, duration: null, channel: 'ch', score: 9, bestMatch: true }],
    } as never)
    const shows = deferredList()
    vi.mocked(api.showsApi.list).mockReturnValue(shows.promise as never)

    renderPage()
    await waitFor(() => expect(api.moviesApi.autoDownload).toHaveBeenCalledWith('a'))
    await screen.findByText(/no suitable match found/i)

    // Switch to Shows. The shows list is still in flight, so the only truthful
    // render is "loading" -- and no effect may treat movie A as a show.
    await user.click(screen.getByRole('button', { name: /^Shows$/ }))
    await act(async () => { await Promise.resolve(); await Promise.resolve() })

    // The torn beat must produce no shows-side traffic for the movie's id:
    // no search (auto or otherwise), and above all no download.
    expect(api.showsApi.search).not.toHaveBeenCalled()
    expect(api.showsApi.download).not.toHaveBeenCalled()

    // Once the real shows list lands, auto mode acts on the real show -- the
    // guard must cost nothing but the torn beat.
    await act(async () => { shows.resolve([item('s1', 'Show One', 2004)]) })
    await waitFor(() => expect(api.showsApi.download).toHaveBeenCalledWith('s1', 'v-best'))
  })
})
