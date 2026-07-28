import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')
const QueuePage = (await import('@/app/queue/page')).default

const item = (over: Record<string, unknown>) => ({
  id: 'x', source: 'plex', sourceRef: 'r', year: 2002, sourcePath: '/p',
  folderName: '/p', posterUrl: null, status: 'pending', ...over,
})

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({ selectedShowLibraries: { srv1: ['3'] } } as never)
  vi.mocked(api.moviesApi.list).mockResolvedValue([item({ id: 'm1', title: 'A Movie' })] as never)
  vi.mocked(api.showsApi.list).mockResolvedValue([
    item({ id: 's1', title: 'The Wire', plexHasTheme: false }),
    item({ id: 's2', title: 'Severance', status: 'plexTheme', plexHasTheme: true }),
  ] as never)
  // The queue auto-searches whatever it lands on, so both paths need a resolved search
  // or the effect's .then() blows up on an undefined mock return.
  vi.mocked(api.moviesApi.search).mockResolvedValue({ results: [] } as never)
  vi.mocked(api.showsApi.search).mockResolvedValue({ results: [] } as never)
})

function renderPage() {
  return render(<MemoryRouter><AuthProvider><QueuePage /></AuthProvider></MemoryRouter>)
}

describe('Queue media toggle', () => {
  it('defaults to Movies', async () => {
    renderPage()
    await waitFor(() => expect(screen.getAllByText('A Movie').length).toBeGreaterThan(0))
    expect(api.showsApi.list).not.toHaveBeenCalled()
  })

  it('switching to Shows triages shows instead', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(screen.getAllByText('A Movie').length).toBeGreaterThan(0))

    await user.click(screen.getByRole('button', { name: /^Shows$/ }))

    await waitFor(() => expect(screen.getAllByText('The Wire').length).toBeGreaterThan(0))
    // plexTheme shows are not outstanding work — they never enter triage, matching
    // GetPendingShows filtering on plex_has_theme = 0.
    expect(screen.queryByText('Severance')).toBeNull()
  })
})
