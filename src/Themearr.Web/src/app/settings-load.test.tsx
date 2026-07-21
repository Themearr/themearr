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
  vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
  vi.mocked(api.syncApi.start).mockResolvedValue({ started: true } as never)
  // Settings additionally loads the API key and checks RapidAPI status on
  // mount, both unrelated to the loads under test here -- unmocked they'd
  // throw (`.then`/`.catch` on `undefined`) and fail a test for the wrong
  // reason.
  vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'k'.repeat(64) } as never)
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false } as never)
  // A full, valid Settings object so the page (which reads
  // selectedServers/libraryPaths/pathMappings/advanced unconditionally once
  // loaded) doesn't fail for the wrong reason -- a shape mismatch, not the
  // load failure under test.
  vi.mocked(api.settingsApi.get).mockResolvedValue({
    selectedServers: [],
    selectedLibraries: {},
    pathMappings: [],
    libraryPaths: [],
    advanced: { maxSearchDirs: 5, searchDepth: 3 },
    autoDownload: false,
    autoSync: false,
    lastAutoSyncAt: '',
  } as never)
  // Dashboard's load under test.
  vi.mocked(api.statsApi.get).mockResolvedValue({
    total: 0, downloaded: 0, pending: 0, ignored: 0, coverage: 0, addedThisWeek: 0,
    recentActivity: [], recentlyAdded: [],
  } as never)
})

describe('Settings load failures', () => {
  it('shows an error with a retry instead of spinning forever', async () => {
    vi.mocked(api.settingsApi.get).mockRejectedValue(new Error('server down'))
    const { default: SettingsPage } = await import('@/app/settings/page')

    renderPage(<SettingsPage />)

    await waitFor(() => expect(screen.queryByText(/couldn't load|could not load|failed/i)).not.toBeNull())
    expect(screen.queryByRole('button', { name: /retry/i })).not.toBeNull()
  })

  it('a failed version check does not block the rest of Settings', async () => {
    // NOTE: the brief's snippet mocked settingsApi.get with only
    // { autoDownload, autoSync }, but SettingsPage unconditionally reads
    // selectedServers/pathMappings/libraryPaths/advanced once settings is
    // non-null -- a partial object crashes the render for an unrelated
    // reason (Cannot read properties of undefined) rather than exercising
    // what this test is actually about. Filled in to a full Settings object,
    // keeping autoDownload/autoSync as specified.
    vi.mocked(api.settingsApi.get).mockResolvedValue({
      selectedServers: [], selectedLibraries: {}, pathMappings: [], libraryPaths: [],
      advanced: { maxSearchDirs: 5, searchDepth: 3 },
      autoDownload: false, autoSync: false, lastAutoSyncAt: '',
    } as never)
    vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: true } as never)
    vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'k'.repeat(64) } as never)
    vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
    vi.mocked(api.versionApi.get).mockRejectedValue(new Error('github down'))
    const { default: SettingsPage } = await import('@/app/settings/page')

    renderPage(<SettingsPage />)

    // The page still works: a settings control is present despite the version
    // failure. queryAllByText rather than queryByText -- "RapidAPI" appears
    // several times on the page (the section heading, its hint, the setup
    // instructions), and queryByText throws on multiple matches instead of
    // returning one.
    await waitFor(() => expect(screen.queryAllByText(/RapidAPI/i).length).toBeGreaterThan(0))
  })
})

describe('Dashboard load failure', () => {
  it('shows an error rather than an empty dashboard', async () => {
    vi.mocked(api.statsApi.get).mockRejectedValue(new Error('server down'))
    const { default: DashboardPage } = await import('@/app/dashboard/page')

    renderPage(<DashboardPage />)

    await waitFor(() => expect(screen.queryByText(/couldn't load|could not load|failed/i)).not.toBeNull())
  })
})
