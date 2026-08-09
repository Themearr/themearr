import { act, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')

// The pages render inside AppShell, which guards on useAuth() (loading/authorized)
// before rendering children at all, so the wrapper needs the auth context as well
// as a router.
function renderPage(ui: React.ReactElement) {
  return render(
    <MemoryRouter>
      <AuthProvider>{ui}</AuthProvider>
    </MemoryRouter>,
  )
}

const movie = (id: string, title: string, year: number) => ({
  id, source: 'plex', sourceRef: `r-${id}`, title, year,
  sourcePath: null, folderName: title, status: 'pending', posterUrl: null,
})

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)

  vi.mocked(api.moviesApi.list).mockResolvedValue([movie('a', 'Movie A', 2001)] as never)
  // Auto mode on -- the auto-download effect only fires when it is.
  vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: true } as never)
  vi.mocked(api.moviesApi.search).mockResolvedValue({ movie: {}, results: [] } as never)
})

describe('auto mode does not retry a failed auto-download forever', () => {
  it('calls autoDownload exactly once when it rejects, across several separate commits', async () => {
    vi.mocked(api.moviesApi.autoDownload).mockRejectedValue(
      new Error('No suitable match found — please select manually.'),
    )

    const { default: QueuePage } = await import('@/app/queue/page')
    renderPage(<QueuePage />)

    // The loop this guards against only reproduces across discrete React commits, not
    // within a single microtask drain -- each failed attempt needs its own render/effect
    // pass to re-check (and, on a regression, clear) the guard. So this flushes in several
    // separate short `act` cycles rather than one long one, giving each possible retry
    // room to actually happen and be counted.
    for (let i = 0; i < 8; i++) {
      await act(async () => {
        await Promise.resolve()
        await Promise.resolve()
      })
    }

    expect(api.moviesApi.autoDownload).toHaveBeenCalledTimes(1)
    // ...and the failure is still on screen -- not wiped by a second silent attempt's
    // own setError('') at the top of the effect.
    expect(screen.queryByText(/no suitable match found/i)).not.toBeNull()
  })
})
