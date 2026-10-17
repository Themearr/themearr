import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthProvider } from '@/lib/auth'
vi.mock('@/lib/api', async () => ({ ...(await import('@/test/apiMock')).makeApiMock(), recheckApi: Object.fromEntries(['start', 'status', 'flagged', 'keep', 'replace'].map(name => [name, vi.fn()])) }))
const api = await import('@/lib/api')
const recheck = (api as unknown as Record<string, Record<string, ReturnType<typeof vi.fn>>>).recheckApi
const Queue = (await import('./queue/page')).default
const Dashboard = (await import('./dashboard/page')).default
const flags = [
  { id: 'a', mediaType: 'movie', title: 'Arrival', year: 2016, themeTitle: 'Arrival Trailer', reason: 'looks like a trailer, clip or promo', state: 'review' },
  { id: 'b', mediaType: 'show', title: 'Severance', year: 2022, themeTitle: 'Severance score', reason: 'theme is too long', state: 'queued' },
]
function page(ui: React.ReactNode) { return render(<MemoryRouter><AuthProvider>{ui}</AuthProvider></MemoryRouter>) }
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ setupComplete: true, plexConnected: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false } as never)
  vi.mocked(api.moviesApi.list).mockResolvedValue([]); vi.mocked(api.showsApi.list).mockResolvedValue([])
  recheck.flagged.mockResolvedValue(flags); recheck.keep.mockResolvedValue({ state: 'kept' }); recheck.replace.mockResolvedValue({ state: 'queued' }); recheck.start.mockResolvedValue({ started: true }); recheck.status.mockResolvedValue({ inProgress: false, finished: true, scanned: 2, flagged: 2, error: '' })
})
describe('recheck is accessible through shipped pages', () => {
  it('shows flagged movies and shows when the pending queue is empty', async () => {
    page(<Queue />); const region = await screen.findByRole('region', { name: /needs attention/i }); await within(region).findByText('Arrival')
    for (const text of ['Arrival', 'Severance', 'Arrival Trailer', 'theme is too long']) expect(within(region).getByText(text)).toBeInTheDocument()
  })
  it('keeps one item without removing another media type', async () => {
    page(<Queue />); const region = await screen.findByRole('region', { name: /needs attention/i }); await within(region).findByText('Arrival'); recheck.flagged.mockResolvedValue([flags[1]])
    fireEvent.click(within(region).getAllByRole('button', { name: /keep this/i })[0]); await waitFor(() => expect(within(region).queryByText('Arrival')).not.toBeInTheDocument())
    expect(within(region).getByText('Severance')).toBeInTheDocument(); expect(recheck.keep).toHaveBeenCalledWith('a', 'movie')
  })
  it('requests a replacement without deleting the existing theme', async () => {
    page(<Queue />); const region = await screen.findByRole('region', { name: /needs attention/i }); await within(region).findByText('Arrival'); fireEvent.click(within(region).getAllByRole('button', { name: /find replacement/i })[0])
    await waitFor(() => expect(recheck.replace).toHaveBeenCalledWith('a', 'movie')); expect(api.moviesApi.deleteTheme).not.toHaveBeenCalled(); expect(within(region).getByText('Arrival Trailer')).toBeInTheDocument()
  })
  it('starts a scan and reports its result', async () => {
    page(<Queue />); fireEvent.click(await screen.findByRole('button', { name: /re-check themes/i })); await waitFor(() => expect(screen.getByText(/2 themes checked/i)).toBeInTheDocument(), { timeout: 4000 }); expect(recheck.start).toHaveBeenCalledTimes(1)
  })
  it('shows both dashboard attention counts with queue links', async () => {
    vi.mocked(api.statsApi.get).mockResolvedValue({ total: 5, downloaded: 5, pending: 0, ignored: 0, coverage: 100, addedThisWeek: 0, recentActivity: [], recentlyAdded: [], needsAttention: 2 } as never)
    vi.mocked(api.showsApi.stats).mockResolvedValue({ total: 4, downloaded: 4, plexTheme: 0, pending: 0, ignored: 0, coverage: 100, needsAttention: 3 } as never)
    page(<Dashboard />); expect(await screen.findByRole('link', { name: /2 movie themes need attention/i })).toHaveAttribute('href', '/queue#needs-attention'); expect(screen.getByRole('link', { name: /3 show themes need attention/i })).toHaveAttribute('href', '/queue#needs-attention')
  })
  it('keeps the item visible when saving fails', async () => {
    recheck.keep.mockRejectedValue(new Error('Save failed')); page(<Queue />); const region = await screen.findByRole('region', { name: /needs attention/i }); await within(region).findByText('Arrival')
    await act(async () => { fireEvent.click(within(region).getAllByRole('button', { name: /keep this/i })[0]) }); expect(screen.getByText('Save failed')).toBeInTheDocument(); expect(within(region).getByText('Arrival')).toBeInTheDocument()
  })
})
