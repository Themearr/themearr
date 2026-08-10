import { act, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
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

// Everything here runs on fake timers because both bugs live in the 1s status
// poll. fireEvent + act rather than userEvent, as in queue-race.test.tsx:
// userEvent's own waiting doesn't see vitest's fake clock.
function flush(ms: number) {
  return act(async () => { await vi.advanceTimersByTimeAsync(ms) })
}

const item = (id: string, title: string, year: number) => ({
  id, source: 'plex', sourceRef: `r-${id}`, title, year,
  sourcePath: null, folderName: title, status: 'pending', posterUrl: null,
})

// What the movie download's status endpoint reports next; tests mutate this to
// fail or finish the download at a chosen moment, mid-poll, exactly like #43's
// deferred rejections did for the request channel.
let movieStatus: { inProgress: boolean; finished: boolean; error: string | null; logs: string[] }

beforeEach(() => {
  vi.clearAllMocks()
  vi.useFakeTimers()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)

  vi.mocked(api.moviesApi.list).mockResolvedValue([
    item('a', 'Movie A', 2001),
    item('b', 'Movie B', 2002),
    item('c', 'Movie C', 2003),
  ] as never)
  vi.mocked(api.showsApi.list).mockResolvedValue([item('s1', 'Show One', 2004)] as never)
  // Auto mode off: after the poll settles a failure there is no converging
  // writer left, so whatever the settle wrote IS the settled DOM (the
  // queue-stale-error harness technique).
  vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false } as never)
  vi.mocked(api.moviesApi.search).mockResolvedValue({
    movie: {},
    results: [{ videoId: 'v1', title: 'A theme', thumbnail: null, duration: null, channel: 'ch', score: 1, bestMatch: false }],
  } as never)
  vi.mocked(api.showsApi.search).mockResolvedValue({ results: [] } as never)
  vi.mocked(api.moviesApi.download).mockResolvedValue({ started: true, movieId: 'a' } as never)

  movieStatus = { inProgress: true, finished: false, error: null, logs: [] }
  vi.mocked(api.moviesApi.downloadStatus).mockImplementation((() => Promise.resolve(movieStatus)) as never)
  // What the real endpoint says about an id with no active download
  // (DownloadService.GetStatus, DownloadService.cs:117): not in progress, and
  // never "finished" -- so a poll pointed at the wrong media's endpoint waits
  // forever rather than erroring.
  vi.mocked(api.showsApi.downloadStatus).mockResolvedValue(
    { inProgress: false, finished: false, error: null, logs: [] } as never,
  )
})

afterEach(() => {
  vi.useRealTimers()
})

// Starts a download for the movie currently at the head of the queue by clicking
// the Download button on the first YouTube result (the manual-URL form has a
// second "Download" button, which is disabled while its input is empty).
async function startDownload() {
  const buttons = screen.getAllByRole('button', { name: /^download$/i })
  await act(async () => { fireEvent.click(buttons[0]) })
}

// The request channel's four catch sites got the identity gate in #43; the
// status poll is the OTHER way a failure reaches the page, and it must obey the
// same rule: an error belongs to the item (and attempt) it was reported for,
// not to whatever the user browsed to since.
describe("the status poll's server-reported failure obeys the #43 identity gate", () => {
  it('does not put the error under the movie the user browsed to', async () => {
    renderPage()
    await flush(50)
    await startDownload()
    // One healthy tick so the poll is genuinely running before the user moves.
    await flush(1100)

    // Browse to Movie B while A's download runs -- the "Up next" rows are
    // deliberately not disabled during a download.
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /movie b/i })) })
    await flush(50)
    expect(screen.queryByText('2 movies left in queue')).not.toBeNull()

    // The server now reports A's download failed; the next tick delivers it.
    movieStatus = { inProgress: false, finished: true, error: 'yt-dlp exploded on Movie A', logs: [] }
    await flush(1100)

    // A's failure must not render under B...
    expect(screen.queryByText(/yt-dlp exploded on Movie A/i)).toBeNull()
    // ...but suppressing the banner must not cost the recovery: the download
    // is over, so Skip re-enables rather than staying wedged.
    expect(screen.getByRole('button', { name: /^skip$/i })).not.toBeDisabled()
  })

  it('still shows the error when its movie is the one on screen', async () => {
    renderPage()
    await flush(50)
    await startDownload()

    movieStatus = { inProgress: false, finished: true, error: 'yt-dlp exploded on Movie A', logs: [] }
    await flush(1100)

    // Still on Movie A: the failure is exactly where it belongs, and the gate
    // must not suppress a legitimately-placed error.
    expect(screen.queryByText(/yt-dlp exploded on Movie A/i)).not.toBeNull()
    expect(screen.getByRole('button', { name: /^skip$/i })).not.toBeDisabled()
  })
})

// The poll used to read the *live* adapter, so toggling movies<->shows mid-
// download re-pointed it at the other media's status endpoint -- which reports
// "no such download, not finished" forever (DownloadService.GetStatus's
// unknown-id shape) and wedges the queue in "Downloading…". The poll must stay
// bound to the media the download started under.
describe('a running download keeps polling the media it started under', () => {
  it('keeps hitting the movie status endpoint after a mid-download switch to Shows', async () => {
    renderPage()
    await flush(50)
    await startDownload()
    await flush(1100)
    const ticksBeforeSwitch = vi.mocked(api.moviesApi.downloadStatus).mock.calls.length
    expect(ticksBeforeSwitch).toBeGreaterThan(0)

    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /^Shows$/ })) })
    await flush(50)
    expect(screen.queryByText('1 show left in queue')).not.toBeNull()

    await flush(3000)
    // The movie download's poll must not have asked the shows endpoint about it...
    expect(api.showsApi.downloadStatus).not.toHaveBeenCalled()
    // ...and must still be tracking it on the movies endpoint.
    expect(vi.mocked(api.moviesApi.downloadStatus).mock.calls.length).toBeGreaterThan(ticksBeforeSwitch)
  })

  it('a download finishing while Shows is on screen neither advances the show queue nor wedges it', async () => {
    renderPage()
    await flush(50)
    await startDownload()
    await flush(1100)

    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /^Shows$/ })) })
    await flush(50)
    expect(screen.queryByText('1 show left in queue')).not.toBeNull()

    // A's download completes successfully while the user triages shows.
    movieStatus = { inProgress: false, finished: true, error: null, logs: [] }
    await flush(1100)

    // The show was not silently skipped: with exactly one pending show, a
    // wrongful advance would flip the page to "All caught up!".
    expect(screen.queryByText('1 show left in queue')).not.toBeNull()
    expect(screen.queryByText(/all caught up/i)).toBeNull()
    // And tracking ended -- the queue is not wedged behind `downloading`.
    expect(screen.getByRole('button', { name: /^skip$/i })).not.toBeDisabled()

    // Switching back converges on the server's truth: A is downloaded now, so
    // the refetched movie queue starts at B.
    vi.mocked(api.moviesApi.list).mockResolvedValue([
      { ...item('a', 'Movie A', 2001), status: 'downloaded' },
      item('b', 'Movie B', 2002),
      item('c', 'Movie C', 2003),
    ] as never)
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /^Movies$/ })) })
    await flush(50)
    expect(screen.queryByText('2 movies left in queue')).not.toBeNull()
  })

  it('a download surviving a switch away and back settles normally', async () => {
    renderPage()
    await flush(50)
    await startDownload()
    await flush(1100)

    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /^Shows$/ })) })
    await flush(1100)
    expect(screen.queryByText('1 show left in queue')).not.toBeNull()

    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /^Movies$/ })) })
    await flush(50)
    // Back on Movies mid-download: A is on the card and still tracking.
    expect(screen.queryByText(/downloading theme/i)).not.toBeNull()

    movieStatus = { inProgress: false, finished: true, error: null, logs: [] }
    await flush(1100)

    // The ordinary success path: the queue advanced off A and unwedged.
    expect(screen.queryByText('2 movies left in queue')).not.toBeNull()
    expect(screen.getByRole('button', { name: /^skip$/i })).not.toBeDisabled()
  })
})
