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

const task = (over: Record<string, unknown> = {}) => ([{
  id: 'syncShows', name: 'Sync Shows', interval: '1.00:00:00',
  lastRunUtc: null, lastDurationMs: null, lastResult: null,
  nextRunUtc: null, isRunning: false, ...over,
}])

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({ selectedShowLibraries: { srv1: ['3'] } } as never)
  vi.mocked(api.showsApi.list).mockResolvedValue([] as never)
  vi.mocked(api.systemApi.runTask).mockResolvedValue({ started: true } as never)
  vi.mocked(api.systemApi.tasks).mockResolvedValue(task() as never)
})

/**
 * A show sync makes one Plex request per show, so a large library takes a minute. The
 * page previously showed only a button spinner while its body still read "No shows yet"
 * — actively contradicting what was happening, which reads as "the button is broken".
 * Reported from a real install after the sync had in fact worked.
 */
describe('Shows page sync feedback', () => {
  it('says it is syncing, instead of claiming there are no shows', async () => {
    const user = userEvent.setup()
    // Still running when polled, so the syncing state stays on screen.
    vi.mocked(api.systemApi.tasks).mockResolvedValue(task({ isRunning: true }) as never)

    renderPage()
    await waitFor(() => expect(api.showsApi.list).toHaveBeenCalled())
    await user.click(screen.getByRole('button', { name: /Sync shows/i }))

    await waitFor(() => expect(screen.getByText(/Syncing shows from Plex/i)).toBeTruthy())
    // The contradiction that made this look broken.
    expect(screen.queryByText(/No shows yet/i)).toBeNull()
    expect(screen.queryByText(/No show libraries selected/i)).toBeNull()
  })

  it('reports what the sync actually did when it finishes', async () => {
    const user = userEvent.setup()
    vi.mocked(api.systemApi.tasks).mockResolvedValue(
      task({ isRunning: false, lastResult: 'synced 253 shows' }) as never)

    renderPage()
    await waitFor(() => expect(api.showsApi.list).toHaveBeenCalled())
    await user.click(screen.getByRole('button', { name: /Sync shows/i }))

    // The task registry already stores this string; the page used to discard it.
    await waitFor(() => expect(screen.getByText(/synced 253 shows/i)).toBeTruthy(), { timeout: 4000 })
  })
})
