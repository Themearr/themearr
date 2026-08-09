import { render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')

const entries = [
  { id: 2, movieId: 's1', movieTitle: 'Beyond Paradise', movieYear: 2023,
    themeTitle: 'Beyond Paradise Theme', sourceUrl: null,
    downloadedAt: '2026-08-09T00:00:00Z', mediaType: 'show' },
  { id: 1, movieId: 'm1', movieTitle: 'Project Hail Mary', movieYear: 2026,
    themeTitle: 'Life is Reason', sourceUrl: null,
    downloadedAt: '2026-08-08T00:00:00Z', mediaType: 'movie' },
]

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.showsApi.stats).mockResolvedValue({
    total: 0, downloaded: 0, plexTheme: 0, pending: 0, ignored: 0, coverage: 0,
  } as never)
  vi.mocked(api.statsApi.get).mockResolvedValue({
    total: 10, downloaded: 10, pending: 0, ignored: 0, coverage: 100, addedThisWeek: 1,
    recentActivity: entries, recentlyAdded: [],
  } as never)
  vi.mocked(api.historyApi.get).mockResolvedValue(entries as never)
})

function renderPage(ui: React.ReactElement) {
  return render(<MemoryRouter><AuthProvider>{ui}</AuthProvider></MemoryRouter>)
}

/**
 * Scopes assertions to one entry's title line. getByText matches the <p> exactly even
 * though it also contains the year span and the badge: Testing Library's getNodeText reads
 * only an element's DIRECT text children, and both of those are nested in spans.
 */
const titleLine = (title: string) => within(screen.getByText(title))

describe('show themes are labelled in download history', () => {
  it('badges the show row and not the movie row on the dashboard', async () => {
    const { default: DashboardPage } = await import('@/app/dashboard/page')
    renderPage(<DashboardPage />)

    await waitFor(() => expect(screen.getByText('Beyond Paradise')).toBeTruthy())

    expect(titleLine('Beyond Paradise').getByText('Show')).toBeTruthy()
    expect(titleLine('Project Hail Mary').queryByText('Show')).toBeNull()
  })

  it('badges the show row and not the movie row on the History page', async () => {
    const { default: HistoryPage } = await import('@/app/history/page')
    renderPage(<HistoryPage />)

    await waitFor(() => expect(screen.getByText('Beyond Paradise')).toBeTruthy())

    expect(titleLine('Beyond Paradise').getByText('Show')).toBeTruthy()
    expect(titleLine('Project Hail Mary').queryByText('Show')).toBeNull()
  })
})
