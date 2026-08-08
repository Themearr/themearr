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
      { key: '1', title: 'Films', type: 'movie' },
      { key: '3', title: 'TV Shows', type: 'show' },
    ] },
  } as never)
  vi.mocked(api.settingsApi.save).mockResolvedValue({} as never)
  vi.mocked(api.syncApi.start).mockResolvedValue({ started: true } as never)
  vi.mocked(api.systemApi.runTask).mockResolvedValue({ started: true } as never)
})

function renderPage() {
  return render(<MemoryRouter><AuthProvider><SettingsPage /></AuthProvider></MemoryRouter>)
}

describe('library save offers a sync', () => {
  it('is not shown before saving', async () => {
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('Films')).toBeTruthy())

    expect(screen.queryByRole('button', { name: /Sync now/i })).toBeNull()
  })

  it('offers a movie sync after saving movie libraries, and starts it', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('Films')).toBeTruthy())

    await user.click(screen.getByRole('button', { name: /Save movie libraries/i }))
    await waitFor(() => expect(screen.getByRole('button', { name: /Sync now/i })).toBeTruthy())

    await user.click(screen.getByRole('button', { name: /Sync now/i }))

    await waitFor(() => expect(api.syncApi.start).toHaveBeenCalled())
    // Movies use the ordinary sync, never the shows task.
    expect(api.systemApi.runTask).not.toHaveBeenCalled()
  })

  it('offers a show sync after saving show libraries, using the syncShows task', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('TV Shows')).toBeTruthy())

    await user.click(screen.getByRole('button', { name: /Save show libraries/i }))
    await waitFor(() => expect(screen.getByRole('button', { name: /Sync now/i })).toBeTruthy())

    await user.click(screen.getByRole('button', { name: /Sync now/i }))

    await waitFor(() => expect(api.systemApi.runTask).toHaveBeenCalledWith('syncShows'))
    expect(api.syncApi.start).not.toHaveBeenCalled()
  })

  /**
   * Both prompts can be on screen at once. A shared started-flag would let the movie
   * sync mark the show prompt as "Sync started ✓" — claiming a sync that never ran.
   */
  it('starting one sync does not mark the other as started', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(screen.getByLabelText('Films')).toBeTruthy())

    await user.click(screen.getByRole('button', { name: /Save movie libraries/i }))
    await user.click(screen.getByRole('button', { name: /Save show libraries/i }))
    await waitFor(() => expect(screen.getAllByRole('button', { name: /Sync now/i })).toHaveLength(2))

    // Start only the movie one (Movie Libraries renders first, so index 0).
    await user.click(screen.getAllByRole('button', { name: /Sync now/i })[0])

    await waitFor(() => expect(api.syncApi.start).toHaveBeenCalled())
    // The show section must still be offering its sync, not claiming one ran.
    expect(screen.getAllByRole('button', { name: /Sync now/i })).toHaveLength(1)
    expect(screen.getAllByText(/Sync started/i)).toHaveLength(1)
    expect(api.systemApi.runTask).not.toHaveBeenCalled()
  })
})
