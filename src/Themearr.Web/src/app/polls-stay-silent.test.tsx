import { act, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
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

// The System page's task poll runs on a real 10s setInterval (see
// system/page.tsx), and the Sidebar rendered alongside it polls sync status
// every 3s -- both need to actually elapse for this test to mean anything,
// so it runs on fake timers rather than a fixed real-time wait.
function flush(ms: number) {
  return act(async () => { await vi.advanceTimersByTimeAsync(ms) })
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.useFakeTimers()
  // AuthProvider's mount-time verification.
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  // Sidebar (rendered by AppShell alongside every page) polls these itself.
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
})

afterEach(() => {
  vi.useRealTimers()
})

describe('a deliberately silent background poll', () => {
  // Five poll sites are supposed to swallow a failed refresh rather than blank an
  // already-loaded view: settings' update-status poll, settings' version re-check,
  // movies' sync-status poll, system's task poll, and login's Plex PIN poll. That
  // was a deliberate fix (a dropped request is worse handled as "stale but still
  // showing" than as "reload and lose everything"), and nothing else in the test
  // suite pins it -- so a well-meaning "let's surface poll errors too" change could
  // silently reintroduce the bug this file guards against, exercised here via the
  // System page's task poll.
  it('a failed background poll does not blank an already-loaded page', async () => {
    vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
    vi.mocked(api.systemApi.tasks)
      .mockResolvedValueOnce([{
        id: 'syncLibrary', name: 'Sync Library', interval: '1.00:00:00',
        lastRunUtc: null, lastDurationMs: null, lastResult: null, nextRunUtc: null, isRunning: false,
      }] as never)
      .mockRejectedValue(new Error('dropped poll'))
    const { default: SystemPage } = await import('@/app/system/page')

    renderPage(<SystemPage />)
    await flush(50)

    // The task row renders on the Tasks tab, not the default Health tab.
    fireEvent.click(screen.getByRole('tab', { name: /tasks/i }))
    expect(screen.getByText('Sync Library')).not.toBeNull()

    // Advance well past the page's 10s poll interval; every poll from here on
    // is mocked to reject.
    await flush(10500)

    // A later poll fails; the row that was already loaded must survive.
    expect(screen.queryByText('Sync Library')).not.toBeNull()
  })

  it("the movies sync-status poll failing does not blank the grid or surface an error", async () => {
    // This sits right next to the manual Sync button, whose failure IS surfaced.
    // The poll must stay on the other side of that line: a dropped status check
    // is not something to shout about, and must never blank the loaded grid.
    vi.mocked(api.moviesApi.list).mockResolvedValue([
      { id: 'a', source: 'plex', sourceRef: 'ra', title: 'Movie A', year: 2001, sourcePath: null, folderName: 'Movie A', status: 'pending', posterUrl: null },
    ] as never)
    vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
    vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
    vi.mocked(api.syncApi.start).mockResolvedValue({ started: true } as never)
    // The poll itself fails on every tick.
    vi.mocked(api.syncApi.status).mockRejectedValue(new Error('dropped poll'))
    const { default: MoviesPage } = await import('@/app/movies/page')

    renderPage(<MoviesPage />)
    await flush(50)
    expect(screen.queryAllByText('Movie A').length).toBeGreaterThan(0)

    // Start a sync so the status poll runs, then let several ticks fail.
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /sync/i })) })
    await flush(6000)

    // Grid intact, and the silent poll surfaced nothing (only the manual button would).
    expect(screen.queryAllByText('Movie A').length).toBeGreaterThan(0)
    expect(screen.queryByText(/couldn't/i)).toBeNull()
  })
})
