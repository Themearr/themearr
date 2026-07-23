import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
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
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
  vi.mocked(api.syncApi.start).mockResolvedValue({ started: true } as never)
})

describe('an action that fails does not report success', () => {
  it('Remove does not claim the RapidAPI key is gone', async () => {
    const user = userEvent.setup()
    vi.mocked(api.settingsApi.get).mockResolvedValue({
      selectedServers: [], selectedLibraries: {}, pathMappings: [], libraryPaths: [],
      advanced: { maxSearchDirs: 5, searchDepth: 3 },
      autoDownload: false, autoSync: false, lastAutoSyncAt: '',
    } as never)
    vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: true } as never)
    vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'k'.repeat(64) } as never)
    vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
    vi.mocked(api.rapidApiApi.remove).mockRejectedValue(new Error('server down'))
    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    // "Remove" also matches the icon-only remove buttons on the Local Library
    // Paths row (aria-label="Remove"), so disambiguate by visible text -- the
    // RapidAPI section's is a labelled <Button>, the path row's is bare SVG.
    const removeCandidates = await screen.findAllByRole('button', { name: /remove/i })
    const remove = removeCandidates.find(b => b.textContent === 'Remove')
    expect(remove).toBeDefined()
    await user.click(remove!)

    // The key is still stored, so the UI must not say otherwise.
    await waitFor(() => expect(screen.queryByText(/couldn't|could not|failed/i)).not.toBeNull())
    // ...and must keep saying a key IS configured, not fall back to the "add a key" form.
    expect(screen.queryByText(/API key configured/i)).not.toBeNull()
    expect(screen.queryAllByRole('button', { name: /remove/i }).some(b => b.textContent === 'Remove')).toBe(true)
  })

  it('the Auto toggle does not stay on when the save fails', async () => {
    const user = userEvent.setup()
    // A pending movie is required for the Auto toggle to render at all -- an
    // empty library sends Queue down the "All caught up!" branch, which has
    // no header actions (and thus no Auto control) at all.
    vi.mocked(api.moviesApi.list).mockResolvedValue([
      { id: 'm1', source: 'plex', sourceRef: 'r1', title: 'Movie 1', year: 2020, sourcePath: null, folderName: 'Movie 1', status: 'pending', posterUrl: null },
    ] as never)
    vi.mocked(api.moviesApi.search).mockResolvedValue({ movie: {}, results: [] } as never)
    // The pre-fix implementation optimistically flips `autoMode` to true before
    // the save settles, which (with a pending movie in view) fires the
    // auto-download effect below -- mock it so that unrelated path doesn't
    // throw and mask the failure this test is actually about.
    vi.mocked(api.moviesApi.autoDownload).mockResolvedValue({ started: true, movieId: 'm1', videoId: 'v1', videoTitle: 't' } as never)
    vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false } as never)
    vi.mocked(api.settingsApi.save).mockRejectedValue(new Error('server down'))
    const { default: QueuePage } = await import('@/app/queue/page')
    renderPage(<QueuePage />)

    const toggle = await screen.findByRole('button', { name: /auto/i })
    await user.click(toggle)

    await waitFor(() => expect(screen.queryByText(/couldn't|could not|failed/i)).not.toBeNull())

    // Behavioural check that the toggle didn't silently flip to "on": if it had,
    // a second click would try to turn it back *off* (autoDownload: false). Since
    // the first save failed, the control must still think it's off, so a second
    // click tries to turn it on again.
    await waitFor(() => expect(api.settingsApi.save).toHaveBeenCalledTimes(1))
    expect(api.settingsApi.save).toHaveBeenLastCalledWith(expect.objectContaining({ autoDownload: true }))

    await user.click(toggle)
    await waitFor(() => expect(api.settingsApi.save).toHaveBeenCalledTimes(2))
    expect(api.settingsApi.save).toHaveBeenLastCalledWith(expect.objectContaining({ autoDownload: true }))
  })

  it('a failed "check for updates" says so instead of going quiet', async () => {
    const user = userEvent.setup()
    vi.mocked(api.settingsApi.get).mockResolvedValue({
      selectedServers: [], selectedLibraries: {}, pathMappings: [], libraryPaths: [],
      advanced: { maxSearchDirs: 5, searchDepth: 3 },
      autoDownload: false, autoSync: false, lastAutoSyncAt: '',
    } as never)
    vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false } as never)
    vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'k'.repeat(64) } as never)
    vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
    vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
    vi.mocked(api.versionApi.refresh).mockRejectedValue(new Error('github down'))
    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    const check = await screen.findByRole('button', { name: /check for updates/i })
    await user.click(check)

    await waitFor(() => expect(screen.queryByText(/couldn't|could not|failed/i)).not.toBeNull())
  })

  it('the movies Sync button says so when the sync fails to start', async () => {
    const user = userEvent.setup()
    // A non-empty library, so the empty-library auto-sync path (which is
    // deliberately silent) doesn't fire and mask the manual button under test.
    vi.mocked(api.moviesApi.list).mockResolvedValue([
      { id: 'm1', source: 'plex', sourceRef: 'r1', title: 'Movie 1', year: 2020, sourcePath: null, folderName: 'Movie 1', status: 'pending', posterUrl: null },
    ] as never)
    vi.mocked(api.syncApi.start).mockRejectedValue(new Error('Could not reach the server'))
    const { default: MoviesPage } = await import('@/app/movies/page')
    renderPage(<MoviesPage />)

    const sync = await screen.findByRole('button', { name: /sync/i })
    await user.click(sync)

    await waitFor(() => expect(screen.queryByText(/couldn't start sync/i)).not.toBeNull())
    // And it must not get stuck pretending it's still syncing.
    expect(screen.queryByRole('button', { name: /syncing/i })).toBeNull()
  })

  it('the queue Ignore button says so and does not advance when the ignore fails', async () => {
    const user = userEvent.setup()
    vi.mocked(api.moviesApi.list).mockResolvedValue([
      { id: 'm1', source: 'plex', sourceRef: 'r1', title: 'First',  year: 2020, sourcePath: null, folderName: 'First',  status: 'pending', posterUrl: null },
      { id: 'm2', source: 'plex', sourceRef: 'r2', title: 'Second', year: 2021, sourcePath: null, folderName: 'Second', status: 'pending', posterUrl: null },
    ] as never)
    vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false } as never)
    vi.mocked(api.moviesApi.search).mockResolvedValue({ movie: {}, results: [] } as never)
    vi.mocked(api.moviesApi.ignoreMovie).mockRejectedValue(new Error('server down'))
    const { default: QueuePage } = await import('@/app/queue/page')
    renderPage(<QueuePage />)

    // First movie is the current one; two movies are queued.
    expect(await screen.findByText(/2 movies left in queue/i)).not.toBeNull()
    await user.click(screen.getByRole('button', { name: /ignore/i }))

    // The ignore failed, so the movie was NOT recorded ignored: the queue must
    // say so, and must not have quietly advanced past it (which would hide a
    // movie the server still has as pending).
    await waitFor(() => expect(screen.queryByText('server down')).not.toBeNull())
    expect(screen.queryByText(/2 movies left in queue/i)).not.toBeNull()
  })
})
