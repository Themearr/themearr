import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')
const ShowsPage = (await import('@/app/shows/page')).default

function renderPage() {
  return render(<MemoryRouter><AuthProvider><ShowsPage /></AuthProvider></MemoryRouter>)
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.systemApi.tasks).mockResolvedValue([] as never)
})

describe('Shows page', () => {
  it('tells the operator to pick a show library when none are selected', async () => {
    vi.mocked(api.showsApi.list).mockResolvedValue([] as never)
    vi.mocked(api.settingsApi.get).mockResolvedValue({ selectedShowLibraries: {} } as never)

    renderPage()

    await waitFor(() => expect(screen.getByText(/No show libraries selected/i)).toBeTruthy())
    // Anchored on the empty-state's own wording: the sidebar also has a "Settings" link.
    expect(screen.getByRole('link', { name: /Choose them in Settings/i })).toBeTruthy()
  })

  it('lists shows once libraries are selected', async () => {
    vi.mocked(api.settingsApi.get).mockResolvedValue({ selectedShowLibraries: { srv1: ['3'] } } as never)
    vi.mocked(api.showsApi.list).mockResolvedValue([{
      id: 's1', source: 'plex', sourceRef: 'srv1:1', title: 'The Wire', year: 2002,
      sourcePath: '/tv/The Wire', folderName: '/tv/The Wire', posterUrl: null,
      status: 'pending', plexHasTheme: false,
    }] as never)

    renderPage()

    // getAllByText: a card with no poster renders its title twice — once as the poster
    // fallback and once in the caption. Pre-existing MediaCard behaviour.
    await waitFor(() => expect(screen.getAllByText('The Wire').length).toBeGreaterThan(0))
  })

  it('triggers the syncShows task, not the movie sync', async () => {
    const user = userEvent.setup()
    vi.mocked(api.settingsApi.get).mockResolvedValue({ selectedShowLibraries: { srv1: ['3'] } } as never)
    vi.mocked(api.showsApi.list).mockResolvedValue([] as never)
    vi.mocked(api.systemApi.runTask).mockResolvedValue({ started: true } as never)

    renderPage()
    await waitFor(() => expect(api.showsApi.list).toHaveBeenCalled())
    await user.click(screen.getByRole('button', { name: /Sync shows/i }))

    expect(api.systemApi.runTask).toHaveBeenCalledWith('syncShows')
    expect(api.syncApi.start).not.toHaveBeenCalled()
  })
})
