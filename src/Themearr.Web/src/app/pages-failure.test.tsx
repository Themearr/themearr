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
})
