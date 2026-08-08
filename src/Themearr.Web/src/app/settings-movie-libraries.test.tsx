import { render, screen, waitFor, within } from '@testing-library/react'
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
      { key: '1', title: 'Films', type: 'movie' },
      { key: '2', title: 'Kids Films', type: 'movie' },
      { key: '3', title: 'TV Shows', type: 'show' },
    ] },
  } as never)
  vi.mocked(api.settingsApi.save).mockResolvedValue({} as never)
})

function renderPage() {
  return render(<MemoryRouter><AuthProvider><SettingsPage /></AuthProvider></MemoryRouter>)
}

/**
 * Scopes queries to one settings Section. Settings renders two library pickers, so a
 * page-wide assertion can't show that a given section filtered correctly.
 * Section renders <div><div><h2>{title}</h2>…</div>{children}</div>.
 */
function section(title: string) {
  return within(screen.getByRole('heading', { name: title }).parentElement!.parentElement!)
}

describe('Settings movie-library selector', () => {
  it('lists only movie-type libraries, pre-ticked from the stored selection', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('Films')).toBeTruthy())

    const movies = section('Movie Libraries')
    // selectedLibraries was { srv1: ['1'] }, so only Films starts ticked.
    expect((movies.getByLabelText('Films') as HTMLInputElement).checked).toBe(true)
    expect((movies.getByLabelText('Kids Films') as HTMLInputElement).checked).toBe(false)
    // The show library must not leak into the movie picker.
    expect(movies.queryByLabelText('TV Shows')).toBeNull()
  })

  it('saves the selection as selectedLibraries', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('Kids Films')).toBeTruthy())

    await user.click(screen.getByLabelText('Kids Films'))
    await user.click(screen.getByRole('button', { name: /Save movie libraries/i }))

    await waitFor(() => expect(api.settingsApi.save).toHaveBeenCalled())
    const payload = vi.mocked(api.settingsApi.save).mock.calls[0][0]
    expect(payload.selectedLibraries).toEqual({ srv1: ['1', '2'] })
  })

  /** The hint is the only thing telling an operator that unticking is safe. */
  it('explains that unticking never deletes theme files', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('Films')).toBeTruthy())

    expect(screen.getByText(/never deleted from disk/i)).toBeTruthy()
  })

  it('explains itself when the server reports no movie libraries', async () => {
    vi.mocked(api.setupApi.plexLibraries).mockResolvedValue({
      libraries: { srv1: [{ key: '3', title: 'TV Shows', type: 'show' }] },
    } as never)

    renderPage()

    await waitFor(() => expect(screen.getByText(/No movie libraries found/i)).toBeTruthy())
  })
})
