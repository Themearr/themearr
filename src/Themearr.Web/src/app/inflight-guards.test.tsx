import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')

// The pages render inside AppShell, which guards on useAuth() (loading/authorized)
// before rendering children at all, so the wrapper needs the auth context as well
// as a router.
function renderPage(ui: React.ReactElement) {
  return render(
    <MemoryRouter>
      <AuthProvider>{ui}</AuthProvider>
    </MemoryRouter>,
  )
}

// A promise the test resolves by hand, so an action can be held mid-flight for
// as long as the assertions need.
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(res => { resolve = res })
  return { promise, resolve }
}

const FULL_SETTINGS = {
  selectedServers: [], selectedLibraries: {}, pathMappings: [], libraryPaths: [],
  advanced: { maxSearchDirs: 5, searchDepth: 3 },
  autoDownload: false, autoSync: false, lastAutoSyncAt: '',
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
  vi.mocked(api.syncApi.start).mockResolvedValue({ started: true } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue(FULL_SETTINGS as never)
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: true } as never)
  vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'k'.repeat(64) } as never)
})

describe('an action in flight says so and cannot be fired twice', () => {
  it('the Auto toggle shows the save is happening and refuses a second click', async () => {
    const user = userEvent.setup()
    // A pending movie is required for the Auto toggle to render at all -- an
    // empty library sends Queue down the "All caught up!" branch, which has no
    // header actions.
    vi.mocked(api.moviesApi.list).mockResolvedValue([
      { id: 'm1', source: 'plex', sourceRef: 'r1', title: 'Movie 1', year: 2020, sourcePath: null, folderName: 'Movie 1', status: 'pending', posterUrl: null },
    ] as never)
    vi.mocked(api.moviesApi.search).mockResolvedValue({ movie: {}, results: [] } as never)
    vi.mocked(api.moviesApi.autoDownload).mockResolvedValue({ started: true, movieId: 'm1', videoId: 'v1', videoTitle: 't' } as never)
    const save = deferred<unknown>()
    vi.mocked(api.settingsApi.save).mockReturnValue(save.promise as never)

    const { default: QueuePage } = await import('@/app/queue/page')
    renderPage(<QueuePage />)

    const toggle = await screen.findByRole('button', { name: /auto/i })
    await user.click(toggle)
    await waitFor(() => expect(api.settingsApi.save).toHaveBeenCalledTimes(1))

    // The switch itself can't move until the server confirms, so without this
    // the control gives no sign at all that anything is happening.
    expect(toggle).toBeDisabled()
    expect(toggle.querySelector('svg.animate-spin')).not.toBeNull()

    await user.click(toggle)
    expect(api.settingsApi.save).toHaveBeenCalledTimes(1)

    save.resolve(FULL_SETTINGS)
    await waitFor(() => expect(toggle).not.toBeDisabled())
    expect(toggle.querySelector('svg.animate-spin')).toBeNull()
  })

  it('Remove for the RapidAPI key only sends one DELETE per click', async () => {
    const user = userEvent.setup()
    const remove = deferred<unknown>()
    vi.mocked(api.rapidApiApi.remove).mockReturnValue(remove.promise as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    // "Remove" also matches the icon-only remove buttons on the Local Library
    // Paths rows (aria-label="Remove"), so disambiguate by visible text.
    const candidates = await screen.findAllByRole('button', { name: /remove/i })
    const button = candidates.find(b => b.textContent === 'Remove')!
    expect(button).toBeDefined()

    await user.click(button)
    await waitFor(() => expect(api.rapidApiApi.remove).toHaveBeenCalledTimes(1))

    // A second DELETE could resolve while the first is still in flight, leaving
    // the page showing the key as gone *and* an error saying removal failed.
    expect(button).toBeDisabled()
    await user.click(button)
    expect(api.rapidApiApi.remove).toHaveBeenCalledTimes(1)

    remove.resolve({ configured: false })
    await waitFor(() => expect(screen.queryByText(/API key configured/i)).toBeNull())
  })

  it('Retry on a failed movies refresh only issues one request per click', async () => {
    const user = userEvent.setup()
    const reload = deferred<unknown>()
    vi.mocked(api.moviesApi.list)
      .mockResolvedValueOnce([] as never)                 // initial load: genuinely empty
      .mockRejectedValueOnce(new Error('refresh down'))   // the post-sync reload: fails
      .mockReturnValue(reload.promise as never)           // the retry: stays in flight
    vi.mocked(api.syncApi.status).mockResolvedValue(
      { inProgress: false, finished: true, error: '', synced: 500, logs: [] } as never,
    )

    const { default: MoviesPage } = await import('@/app/movies/page')
    renderPage(<MoviesPage />)

    // The empty initial load auto-starts a sync; the 1.5s status poll then sees
    // it finish and triggers the reload that fails, which is what puts the
    // Retry button on screen.
    const retry = await screen.findByRole('button', { name: /retry/i }, { timeout: 5000 })
    await user.click(retry)
    await waitFor(() => expect(api.moviesApi.list).toHaveBeenCalledTimes(3))

    // Concurrent GET /api/movies has no staleness protection, so a slower
    // earlier response could overwrite a newer one.
    expect(retry).toBeDisabled()
    await user.click(retry)
    expect(api.moviesApi.list).toHaveBeenCalledTimes(3)

    // The guard must not cost the retry its effect: once the request lands, the
    // "couldn't refresh" state clears. (The button itself is gone by then, so
    // there's nothing left to assert on it.)
    reload.resolve([])
    await waitFor(() => expect(screen.queryByText(/couldn't refresh/i)).toBeNull())
  }, 10000)
})
