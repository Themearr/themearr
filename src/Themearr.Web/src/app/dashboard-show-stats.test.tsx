import { render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')
const DashboardPage = (await import('@/app/dashboard/page')).default

const movieStats = {
  total: 1451, downloaded: 1264, pending: 187, ignored: 4,
  coverage: 87.1, addedThisWeek: 12, recentActivity: [], recentlyAdded: [],
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.statsApi.get).mockResolvedValue(movieStats as never)
})

function renderPage() {
  return render(<MemoryRouter><AuthProvider><DashboardPage /></AuthProvider></MemoryRouter>)
}

/** The Shows block is a labelled region so assertions can be scoped to it. */
const showsSection = () => within(screen.getByRole('region', { name: 'Shows' }))

describe('Dashboard show stats', () => {
  it('shows nothing about shows on a movie-only install', async () => {
    vi.mocked(api.showsApi.stats).mockResolvedValue({
      total: 0, downloaded: 0, plexTheme: 0, pending: 0, ignored: 0, coverage: 0,
    } as never)

    renderPage()
    await waitFor(() => expect(screen.getByText(/Movie coverage/i)).toBeTruthy())

    expect(screen.queryByRole('region', { name: 'Shows' })).toBeNull()
    expect(screen.queryByText(/Plex theme/i)).toBeNull()
  })

  it('renders show coverage and tiles once shows exist', async () => {
    vi.mocked(api.showsApi.stats).mockResolvedValue({
      total: 253, downloaded: 9, plexTheme: 153, pending: 91, ignored: 0, coverage: 64.0,
    } as never)

    renderPage()
    await waitFor(() => expect(screen.getByRole('region', { name: 'Shows' })).toBeTruthy())

    const shows = showsSection()
    expect(shows.getByText(/Show coverage/i)).toBeTruthy()
    expect(shows.getByText('64%')).toBeTruthy()
    // "covered", not "downloaded" — a plexTheme show counts toward the bar.
    expect(shows.getByText(/162 of 253 shows covered/i)).toBeTruthy()
    expect(shows.getByText('Plex theme')).toBeTruthy()
    expect(shows.getByText('153')).toBeTruthy()
    expect(shows.getByText('91')).toBeTruthy()
  })

  /** /queue would land on the movies queue — its media toggle is component state. */
  it('points every show tile at /shows', async () => {
    vi.mocked(api.showsApi.stats).mockResolvedValue({
      total: 253, downloaded: 9, plexTheme: 153, pending: 91, ignored: 2, coverage: 64.0,
    } as never)

    renderPage()
    await waitFor(() => expect(screen.getByRole('region', { name: 'Shows' })).toBeTruthy())

    // Scoped: the movie tiles carry three of these same four labels, so an unscoped
    // getByText would match two elements and throw.
    for (const label of ['Pending', 'Downloaded', 'Plex theme', 'Ignored']) {
      const tile = showsSection().getByText(label).closest('a')
      expect(tile?.getAttribute('href')).toBe('/shows')
    }
  })

  it('keeps the movie numbers untouched', async () => {
    vi.mocked(api.showsApi.stats).mockResolvedValue({
      total: 253, downloaded: 9, plexTheme: 153, pending: 91, ignored: 0, coverage: 64.0,
    } as never)

    renderPage()
    await waitFor(() => expect(screen.getByText(/Movie coverage/i)).toBeTruthy())

    expect(screen.getByText('87.1%')).toBeTruthy()
    expect(screen.getByText(/1264 of 1451 movies/i)).toBeTruthy()
    expect(screen.getByText('This week')).toBeTruthy()   // movie-only tile, still there
  })
})
