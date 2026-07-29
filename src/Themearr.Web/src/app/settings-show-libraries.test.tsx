import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')
const SettingsPage = (await import('@/app/settings/page')).default

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
  vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'k' } as never)
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({
    selectedServers: [{ id: 'srv1', name: 'Tower', url: 'http://p', urls: ['http://p'] }],
    selectedLibraries: { srv1: ['1'] },
    selectedShowLibraries: {},
    pathMappings: [], libraryPaths: [],
    advanced: { maxSearchDirs: 20000, searchDepth: 4 },
    autoDownload: false, autoSync: false, lastAutoSyncAt: '',
  } as never)
  vi.mocked(api.setupApi.plexLibraries).mockResolvedValue({
    libraries: { srv1: [
      { key: '1', title: 'Movies', type: 'movie' },
      { key: '3', title: 'TV Shows', type: 'show' },
    ] },
  } as never)
  vi.mocked(api.settingsApi.save).mockResolvedValue({} as never)
})

function renderPage() {
  return render(<MemoryRouter><AuthProvider><SettingsPage /></AuthProvider></MemoryRouter>)
}

describe('Settings show-library selector', () => {
  it('lists only show-type libraries', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByLabelText(/TV Shows/i)).toBeTruthy())

    // 'Movies' is a movie-type library and belongs to the existing selector, not this one.
    expect(screen.queryByLabelText(/^Movies$/)).toBeNull()
  })

  it('saves the selection as selectedShowLibraries', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(screen.getByLabelText(/TV Shows/i)).toBeTruthy())

    await user.click(screen.getByLabelText(/TV Shows/i))
    await user.click(screen.getByRole('button', { name: /Save show libraries/i }))

    await waitFor(() => expect(api.settingsApi.save).toHaveBeenCalled())
    const payload = vi.mocked(api.settingsApi.save).mock.calls[0][0]
    expect(payload.selectedShowLibraries).toEqual({ srv1: ['3'] })
  })

  /**
   * The endpoint treats an absent field as "leave unchanged", so the frontend must always
   * send the key once it knows about it — otherwise unticking the last library would look
   * like it saved but leave the old selection stored.
   */
  it('sends an explicit empty map when everything is unticked', async () => {
    const user = userEvent.setup()
    vi.mocked(api.settingsApi.get).mockResolvedValue({
      selectedServers: [{ id: 'srv1', name: 'Tower', url: 'http://p', urls: ['http://p'] }],
      selectedLibraries: { srv1: ['1'] },
      selectedShowLibraries: { srv1: ['3'] },
      pathMappings: [], libraryPaths: [],
      advanced: { maxSearchDirs: 20000, searchDepth: 4 },
      autoDownload: false, autoSync: false, lastAutoSyncAt: '',
    } as never)

    renderPage()
    await waitFor(() => expect(screen.getByLabelText(/TV Shows/i)).toBeTruthy())

    await user.click(screen.getByLabelText(/TV Shows/i))   // untick
    await user.click(screen.getByRole('button', { name: /Save show libraries/i }))

    await waitFor(() => expect(api.settingsApi.save).toHaveBeenCalled())
    const payload = vi.mocked(api.settingsApi.save).mock.calls[0][0]
    expect(payload.selectedShowLibraries).toEqual({ srv1: [] })
  })
})
