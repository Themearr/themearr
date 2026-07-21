import { render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')

// The pages render inside AppShell, which guards on useAuth() (loading/authorized)
// before rendering children at all, so the wrapper needs the auth context as well
// as a router. Without AuthProvider, the default context is stuck at
// `loading: true`, and the page never gets past AppShell's spinner.
function renderPage(ui: React.ReactElement) {
  return render(
    <MemoryRouter>
      <AuthProvider>{ui}</AuthProvider>
    </MemoryRouter>,
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  // Everything a page might poll resolves harmlessly; only the load under test fails.
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  // Movies additionally loads the active library source on mount, and kicks off
  // a sync when the library comes back genuinely empty — both unrelated to the
  // load under test, but unmocked they'd throw (`.then`/`.catch` on `undefined`)
  // and fail the test for the wrong reason.
  vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
  vi.mocked(api.syncApi.start).mockResolvedValue({ started: true } as never)
})

describe('a failed load never renders reassuring copy', () => {
  it('Movies does not claim the library is empty', async () => {
    vi.mocked(api.moviesApi.list).mockRejectedValue(new Error('server down'))
    const { default: MoviesPage } = await import('@/app/movies/page')

    renderPage(<MoviesPage />)

    await waitFor(() => expect(screen.queryByText(/couldn't load|could not load|failed/i)).not.toBeNull())
    expect(screen.queryByText(/No movies yet/i)).toBeNull()
  })

  it('History does not claim there are no downloads', async () => {
    vi.mocked(api.historyApi.get).mockRejectedValue(new Error('server down'))
    const { default: HistoryPage } = await import('@/app/history/page')

    renderPage(<HistoryPage />)

    await waitFor(() => expect(screen.queryByText(/couldn't load|could not load|failed/i)).not.toBeNull())
    expect(screen.queryByText(/No downloads yet/i)).toBeNull()
  })

  it('Queue does not claim everything is caught up', async () => {
    vi.mocked(api.moviesApi.list).mockRejectedValue(new Error('server down'))
    vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false } as never)
    const { default: QueuePage } = await import('@/app/queue/page')

    renderPage(<QueuePage />)

    await waitFor(() => expect(screen.queryByText(/couldn't load|could not load|failed/i)).not.toBeNull())
    expect(screen.queryByText(/All caught up/i)).toBeNull()
  })
})

describe('a successful empty load still shows the empty state', () => {
  it('Movies says the library is empty when it genuinely is', async () => {
    vi.mocked(api.moviesApi.list).mockResolvedValue([] as never)
    const { default: MoviesPage } = await import('@/app/movies/page')

    renderPage(<MoviesPage />)

    await waitFor(() => expect(screen.queryByText(/No movies yet/i)).not.toBeNull())
  })

  it('History says there are no downloads when there genuinely are none', async () => {
    vi.mocked(api.historyApi.get).mockResolvedValue([] as never)
    const { default: HistoryPage } = await import('@/app/history/page')

    renderPage(<HistoryPage />)

    await waitFor(() => expect(screen.queryByText(/No downloads yet/i)).not.toBeNull())
  })

  it('Queue says everything is caught up when the library genuinely has no pending movies', async () => {
    vi.mocked(api.moviesApi.list).mockResolvedValue([] as never)
    vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false } as never)
    const { default: QueuePage } = await import('@/app/queue/page')

    renderPage(<QueuePage />)

    await waitFor(() => expect(screen.queryByText(/All caught up/i)).not.toBeNull())
  })
})

describe('a failed post-sync refresh never renders reassuring copy either', () => {
  // Regression test for the fresh-install path: the initial load succeeds
  // empty, which auto-starts a sync; the sync-status poll sees it finish and
  // reloads the grid -- and that reload is the one that fails here. Before
  // this fix, that failure was swallowed entirely, `movies` stayed `[]`, and
  // the grid rendered "No movies yet" even though the sync had just imported
  // a real library -- with nothing left to ever correct it.
  it('Movies does not claim the library is empty when the post-sync refresh fails', async () => {
    vi.mocked(api.moviesApi.list)
      .mockResolvedValueOnce([] as never)                      // initial load: genuinely empty
      .mockRejectedValueOnce(new Error('refresh down'))        // the post-sync reload: fails
    vi.mocked(api.syncApi.status).mockResolvedValue(
      { inProgress: false, finished: true, error: '', synced: 500, logs: [] } as never,
    )

    const { default: MoviesPage } = await import('@/app/movies/page')
    renderPage(<MoviesPage />)

    // Sanity: we're actually on the auto-sync path this test means to exercise.
    await waitFor(() => expect(api.syncApi.start).toHaveBeenCalled())

    // Wait for the sync-status poll to see `finished` and trigger the reload,
    // which is mocked to fail (the poll runs on a 1.5s interval).
    await waitFor(() => expect(api.moviesApi.list).toHaveBeenCalledTimes(2), { timeout: 5000 })

    // The failed refresh must surface as a notice...
    await waitFor(() => expect(screen.queryByText(/couldn't refresh/i)).not.toBeNull(), { timeout: 3000 })
    // ...but never as the reassuring "empty library" copy.
    expect(screen.queryByText(/No movies yet/i)).toBeNull()
  }, 10000)
})
