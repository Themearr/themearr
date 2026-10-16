import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')
const QueuePage = (await import('@/app/queue/page')).default

// The pages render inside AppShell, which guards on useAuth() (loading/authorized)
// before rendering children at all, so the wrapper needs the auth context as well
// as a router.
function renderPage() {
  return render(
    <MemoryRouter>
      <AuthProvider>
        <QueuePage />
      </AuthProvider>
    </MemoryRouter>,
  )
}

// An ignore the test settles by hand: both bugs here are about what happens
// while the ignore round-trip is still in flight, so it must be held open
// while the test clicks on.
function deferredIgnore() {
  let resolve!: (v: unknown) => void
  let reject!: (e: Error) => void
  const promise = new Promise((res, rej) => { resolve = res; reject = rej })
  return { promise, resolve, reject }
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

  vi.mocked(api.moviesApi.list).mockResolvedValue([
    movie('a', 'Movie A', 2001),
    movie('b', 'Movie B', 2002),
    movie('c', 'Movie C', 2003),
  ] as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false } as never)
  vi.mocked(api.moviesApi.search).mockResolvedValue({ movie: {}, results: [] } as never)
})

describe('Ignore cannot fire twice for one intent', () => {
  it('a double click during a slow ignore sends one request and advances once', async () => {
    const user = userEvent.setup()
    const ignore = deferredIgnore()
    vi.mocked(api.moviesApi.ignoreMovie).mockReturnValue(ignore.promise as never)

    renderPage()
    await screen.findByText('3 movies left in queue')

    const button = screen.getByRole('button', { name: /^ignore$/i })
    await user.click(button)
    await user.click(button)

    await act(async () => { ignore.resolve({ ignored: true }) })

    // One intent, one request, one advance: the queue moved from A to B. A
    // second advance would silently drop B from triage -- the historical
    // silently-skipped-item bug class (queue-race.test.tsx).
    expect(api.moviesApi.ignoreMovie).toHaveBeenCalledTimes(1)
    await waitFor(() => expect(screen.queryByText('2 movies left in queue')).not.toBeNull())
    expect(screen.queryByText('1 movie left in queue')).toBeNull()
  })
})

// The ignore round-trip has the same shape as a download request: it can settle
// after the user browsed on, so its outcome must be checked against what is on
// screen before it acts there (#43's identity rule, through the ignore channel).
describe("a slow ignore's outcome cannot act on the item the user browsed to", () => {
  it('an ignore failure does not put its error under the movie now on screen', async () => {
    const user = userEvent.setup()
    const ignore = deferredIgnore()
    vi.mocked(api.moviesApi.ignoreMovie).mockReturnValue(ignore.promise as never)

    renderPage()
    await screen.findByText('3 movies left in queue')

    await user.click(screen.getByRole('button', { name: /^ignore$/i }))
    await user.click(screen.getByRole('button', { name: /movie b/i }))
    await screen.findByText('2 movies left in queue')

    await act(async () => { ignore.reject(new Error('server down')) })

    // A's failed ignore must not read as Movie B having a problem.
    expect(screen.queryByText('server down')).toBeNull()
    // And nothing is wedged: the button is usable for the item actually shown.
    expect(screen.getByRole('button', { name: /^ignore$/i })).not.toBeDisabled()
  })

  it('an ignore resolving in the effect gap right after Skip cannot advance twice', async () => {
    const user = userEvent.setup()
    const ignore = deferredIgnore()
    vi.mocked(api.moviesApi.ignoreMovie).mockReturnValue(ignore.promise as never)

    renderPage()
    await screen.findByText('3 movies left in queue')

    // Ignore A (in flight), then Skip -- which advances to B immediately.
    await user.click(screen.getByRole('button', { name: /^ignore$/i }))
    const skip = screen.getByRole('button', { name: /^skip$/i })

    // The ignore resolves in the same breath as the Skip click, before React's
    // passive effects re-sync the on-screen key: its success check must not
    // read the stale key, match, and advance a second time -- off B.
    await act(async () => {
      fireEvent.click(skip)
      ignore.resolve({ ignored: true })
      await Promise.resolve()
      await Promise.resolve()
      await Promise.resolve()
    })

    expect(screen.queryByText('2 movies left in queue')).not.toBeNull()
    expect(screen.queryByText('1 movie left in queue')).toBeNull()
  })

  it('an ignore success does not advance the queue past the movie now on screen', async () => {
    const user = userEvent.setup()
    const ignore = deferredIgnore()
    vi.mocked(api.moviesApi.ignoreMovie).mockReturnValue(ignore.promise as never)

    renderPage()
    await screen.findByText('3 movies left in queue')

    await user.click(screen.getByRole('button', { name: /^ignore$/i }))
    // The user browses to Movie B while A's ignore is in flight.
    await user.click(screen.getByRole('button', { name: /movie b/i }))
    await screen.findByText('2 movies left in queue')

    await act(async () => { ignore.resolve({ ignored: true }) })

    // Advancing now would push the user off B without it ever being triaged.
    // A is ignored server-side either way; it leaves the list on the next load.
    expect(screen.queryByText('2 movies left in queue')).not.toBeNull()
    expect(screen.queryByText('1 movie left in queue')).toBeNull()
  })
})
