import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

// Mirror settings-load.test.tsx: use the shared apiMock so every resource the
// page loads on mount (setup status via AuthProvider, version, RapidAPI
// status, the Radarr library source, the API key -- not just settings/plex)
// is stubbed. Without that, the page's other load calls hit the real
// `request()` and the test is noisy/flaky rather than exercising the Plex
// panel.
vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')

// The page renders inside AppShell, which guards on useAuth() (loading/authorized)
// before rendering children at all, so the wrapper needs the auth context as well
// as a router -- same as settings-load.test.tsx's renderPage.
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
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
  vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'k'.repeat(64) } as never)
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({
    selectedServers: [{ id: 'srv1', name: 'Tower', url: 'https://old.plex.direct:32400' }],
    selectedLibraries: {},
    pathMappings: [],
    libraryPaths: [],
    advanced: { maxSearchDirs: 20000, searchDepth: 4 },
    autoDownload: false,
    autoSync: false,
    lastAutoSyncAt: '',
  } as never)
  vi.mocked(api.plexApi.test).mockResolvedValue({ ok: false, detail: 'The Plex server is unreachable.' } as never)
  vi.mocked(api.plexApi.saveUrl).mockResolvedValue({
    selectedServers: [{ id: 'srv1', name: 'Tower', url: 'http://192.168.1.50:32400' }],
  } as never)
})

describe('Plex Connection manual URL', () => {
  it('tests and saves an edited Plex server URL, surfacing the test result', async () => {
    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    const input = await screen.findByDisplayValue('https://old.plex.direct:32400')
    await userEvent.clear(input)
    await userEvent.type(input, 'http://192.168.1.50:32400')

    // Scope the button lookups to the server's own card: "Save"/"Test" also
    // appear elsewhere on the page (the header's "Save changes", the Radarr
    // library-source Save button, RapidAPI's Save/Replace), so an unscoped
    // getByRole would be ambiguous. The server name ("Tower") is unique text
    // inside the card, so anchor there.
    const serverCard = screen.getByText('Tower').closest('div') as HTMLElement

    await userEvent.click(within(serverCard).getByRole('button', { name: /test/i }))
    await waitFor(() => expect(api.plexApi.test).toHaveBeenCalledWith('srv1', 'http://192.168.1.50:32400'))
    expect(await screen.findByText(/unreachable/i)).toBeInTheDocument() // failure surfaced, not swallowed

    await userEvent.click(within(serverCard).getByRole('button', { name: /^save/i }))
    await waitFor(() => expect(api.plexApi.saveUrl).toHaveBeenCalledWith('srv1', 'http://192.168.1.50:32400'))
  })
})
