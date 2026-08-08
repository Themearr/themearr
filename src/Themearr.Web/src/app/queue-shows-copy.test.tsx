import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')
const QueuePage = (await import('@/app/queue/page')).default

const item = (over: Record<string, unknown>) => ({
  id: 'x', source: 'plex', sourceRef: 'r', year: 2023, sourcePath: '/p',
  folderName: '/p', posterUrl: null, status: 'pending', ...over,
})

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({ selectedShowLibraries: { srv1: ['3'] } } as never)
  vi.mocked(api.moviesApi.search).mockResolvedValue({ results: [] } as never)
  vi.mocked(api.showsApi.search).mockResolvedValue({ results: [] } as never)
  vi.mocked(api.moviesApi.list).mockResolvedValue([
    item({ id: 'm1', title: 'A Movie' }), item({ id: 'm2', title: 'Another Movie' }),
  ] as never)
  vi.mocked(api.showsApi.list).mockResolvedValue([
    item({ id: 's1', title: 'Beyond Paradise' }), item({ id: 's2', title: 'The Wire' }),
  ] as never)
})

function renderPage() {
  return render(<MemoryRouter><AuthProvider><QueuePage /></AuthProvider></MemoryRouter>)
}

/**
 * The queue was generalised for shows but its copy stayed hardcoded, so triaging shows
 * read "91 movies left in queue". Reported from a real install.
 */
describe('Queue copy follows the selected media type', () => {
  it('says movies while triaging movies', async () => {
    renderPage()
    await waitFor(() => expect(screen.getAllByText('A Movie').length).toBeGreaterThan(0))

    expect(screen.getByText(/movies left in queue/i)).toBeTruthy()
    expect(screen.queryByText(/shows left in queue/i)).toBeNull()
  })

  it('says shows while triaging shows', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(screen.getAllByText('A Movie').length).toBeGreaterThan(0))

    await user.click(screen.getByRole('button', { name: /^Shows$/ }))

    await waitFor(() => expect(screen.getAllByText('Beyond Paradise').length).toBeGreaterThan(0))
    expect(screen.getByText(/shows left in queue/i)).toBeTruthy()
    expect(screen.queryByText(/movies left in queue/i)).toBeNull()
  })

  it('says shows in the up-next list and the ignore tooltip', async () => {
    const user = userEvent.setup()
    renderPage()
    await waitFor(() => expect(screen.getAllByText('A Movie').length).toBeGreaterThan(0))

    await user.click(screen.getByRole('button', { name: /^Shows$/ }))
    await waitFor(() => expect(screen.getAllByText('Beyond Paradise').length).toBeGreaterThan(0))

    expect(screen.getByText(/Up next · 1 show$/i)).toBeTruthy()
    expect(screen.getByRole('button', { name: /Ignore/i }).getAttribute('title'))
      .toMatch(/this show/i)
  })
})
