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

  it('a success landing after a switch-away-and-back cannot skip the new head of the queue', async () => {
    renderPage()
    await flush(50)
    await startDownload()
    await flush(1100)

    // Away: the user visits Shows while A downloads...
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /^Shows$/ })) })
    await flush(50)
    expect(screen.queryByText('1 show left in queue')).not.toBeNull()

    // ...meanwhile the download completes server-side, and the movie list now
    // reflects it: A is downloaded, gone from pending.
    movieStatus = { inProgress: false, finished: true, error: null, logs: [] }
    vi.mocked(api.moviesApi.list).mockResolvedValue([
      item('b', 'Movie B', 2002),
      item('c', 'Movie C', 2003),
    ] as never)

    // ...and back, before the next poll tick observes the finish. The refetch
    // re-bases the queue on [B, C] with B at the head.
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /^Movies$/ })) })
    await flush(50)
    expect(screen.queryByText('2 movies left in queue')).not.toBeNull()

    // The tick lands. Its advance is for A -- already gone from this list --
    // so a +1 here would silently drop B from triage, the queue-race bug class.
    await flush(1100)

    expect(screen.queryByText('2 movies left in queue')).not.toBeNull()
    expect(screen.queryByText('1 movie left in queue')).toBeNull()
    // Tracking still ended: the queue is not wedged.
    expect(screen.getByRole('button', { name: /^skip$/i })).not.toBeDisabled()
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

// Until the start POST settles, the server's status for this id is the
// PREVIOUS job's: Start() only writes the fresh JobState right before
// returning (DownloadService.cs:70), and the poll begins ticking the moment
// `downloading` flips -- while that POST is still in flight.
describe("the poll cannot act on the previous job's status before the start POST lands", () => {
  it('a retry is not settled by the very failure it is retrying', async () => {
    // The server still remembers the last attempt's terminal state -- exactly
    // what GetStatus serves until the new Start() is processed.
    movieStatus = { inProgress: false, finished: true, error: 'old failure from the last attempt', logs: [] }
    let settleStart!: (v: unknown) => void
    vi.mocked(api.moviesApi.download).mockReturnValue(
      new Promise(res => { settleStart = res }) as never,
    )

    renderPage()
    await flush(50)
    await startDownload()

    // Two ticks pass with the start POST still in flight. Acting on what they
    // read would re-report the old failure and tear down the new attempt
    // while its download proceeds server-side, unobserved.
    await flush(2500)
    expect(screen.queryByText(/old failure from the last attempt/i)).toBeNull()
    expect(screen.getByRole('button', { name: /^skip$/i })).toBeDisabled()

    // The start POST lands; from here the status genuinely describes this
    // attempt, and the ordinary lifecycle resumes.
    movieStatus = { inProgress: true, finished: false, error: null, logs: [] }
    await act(async () => { settleStart({ started: true }) })
    await flush(1100)
    expect(screen.getByRole('button', { name: /^skip$/i })).toBeDisabled()

    movieStatus = { inProgress: false, finished: true, error: null, logs: [] }
    await flush(1100)
    expect(screen.queryByText('2 movies left in queue')).not.toBeNull()
  })

  it('a start POST that never lands hands control back instead of wedging forever', async () => {
    // No previous job: the server answers every status check with the
    // unknown-id shape (DownloadService.cs:117) -- a SUCCESSFUL response that
    // never says finished, so the failing-status escape never triggers.
    movieStatus = { inProgress: false, finished: false, error: null, logs: [] }
    vi.mocked(api.moviesApi.download).mockReturnValue(new Promise(() => {}) as never)

    renderPage()
    await flush(50)
    await startDownload()

    // Long past any reasonable start: the queue must hand control back the
    // same way it does when status checks themselves fail.
    await flush(45000)
    expect(screen.queryByText(/lost contact/i)).not.toBeNull()
    expect(screen.getByRole('button', { name: /^skip$/i })).not.toBeDisabled()
    // And it cannot know the download's fate, so it must not have advanced.
    expect(screen.queryByText('3 movies left in queue')).not.toBeNull()
  })
})

// The 3s auto-skip timer armed by a server-reported failure re-checks the
// attempt and the media at fire time -- but auto mode itself can have been
// turned OFF in those three seconds, which is the user explicitly taking
// manual control of the failed item. No new attempt starts (the auto effect's
// autoTriggeredFor guard holds), so the attempt gate alone cannot catch it.
describe('the auto-mode skip timer respects auto mode being turned off', () => {
  it('does not auto-skip an item the user took manual control of', async () => {
    vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: true } as never)
    vi.mocked(api.settingsApi.save).mockResolvedValue({} as never)
    vi.mocked(api.moviesApi.autoDownload).mockResolvedValue({ started: true } as never)

    renderPage()
    await flush(50)
    expect(api.moviesApi.autoDownload).toHaveBeenCalledWith('a')

    // The server reports the download failed; the settle arms the auto-skip.
    movieStatus = { inProgress: false, finished: true, error: 'A failed', logs: [] }
    await flush(1100)
    expect(screen.queryByText('A failed')).not.toBeNull()

    // The user turns auto off to deal with A by hand. (The toggle's own
    // setError('') clears the banner -- pre-existing design.)
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /auto/i })) })
    await flush(50)

    // The timer fires -- and must not move the queue out from under them.
    await flush(3500)
    expect(screen.queryByText('3 movies left in queue')).not.toBeNull()
    expect(screen.queryByText('2 movies left in queue')).toBeNull()
  })
})

// During the switch-back torn beat, `current` still comes from the OTHER
// library's list while `media` is already back -- and movie/show ids come from
// different tables, so they can collide (the backend namespaces its job keys
// for exactly this, DownloadService.cs JobKey). The on-screen key must not
// vouch for an item the page is not truthfully showing.
describe('the torn beat cannot vouch for a colliding id', () => {
  it('a finish landing in the switch-back beat cannot advance the re-based queue', async () => {
    vi.mocked(api.moviesApi.list).mockResolvedValue([
      item('h', 'Movie H', 2001),
      item('b', 'Movie B', 2002),
      item('c', 'Movie C', 2003),
    ] as never)
    // A show sharing the movie's id, at the head of the shows queue.
    vi.mocked(api.showsApi.list).mockResolvedValue([item('h', 'Show H', 2004)] as never)

    renderPage()
    await flush(50)
    await startDownload()
    await flush(1100)

    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /^Shows$/ })) })
    await flush(50)
    expect(screen.queryByText('1 show left in queue')).not.toBeNull()

    // Switching back: the movies refetch is held in flight, so the torn beat
    // lasts while the poll keeps ticking.
    let landMoviesRefetch!: (v: unknown) => void
    vi.mocked(api.moviesApi.list).mockReturnValue(new Promise(res => { landMoviesRefetch = res }) as never)
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /^Movies$/ })) })

    // The download finishes during the beat. `current` is still Show H --
    // same id, wrong library -- so nothing may treat it as Movie H on screen.
    movieStatus = { inProgress: false, finished: true, error: null, logs: [] }
    await flush(1100)

    // The refetch lands, already excluding the downloaded H: the queue starts
    // at B. An advance during the beat would have re-based onto index 1 --
    // silently dropping B from triage.
    landMoviesRefetch([item('b', 'Movie B', 2002), item('c', 'Movie C', 2003)])
    await flush(50)
    expect(screen.queryByText('2 movies left in queue')).not.toBeNull()
    expect(screen.queryByText('1 movie left in queue')).toBeNull()
  })
})

// The on-screen key syncs via a passive effect; a status tick resuming in the
// commit-to-effect gap after an "Up next" click reads the PREVIOUS key -- which
// matches its own forKey -- and its advance lands on the re-indexed list,
// skipping the item the user just browsed to.
describe('a settle resuming in the effect gap after browsing cannot advance', () => {
  it("a finish tick in the gap does not skip the browsed-to movie", async () => {
    let resolveStatus: ((v: unknown) => void) | null = null
    vi.mocked(api.moviesApi.downloadStatus).mockImplementation((() =>
      new Promise(res => { resolveStatus = res })) as never)

    renderPage()
    await flush(50)
    await startDownload()
    // Tick 1 issues its status request, which we hold open.
    await flush(1100)
    expect(resolveStatus).not.toBeNull()

    // The user browses to Movie B, and the held status resolves "finished" in
    // the same breath -- before React's passive effects re-sync the key.
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: /movie b/i }))
      resolveStatus!({ inProgress: false, finished: true, error: null, logs: [] })
      await Promise.resolve()
      await Promise.resolve()
      await Promise.resolve()
    })

    // B is on the card and must still be: the finish belonged to A.
    expect(screen.queryByText('2 movies left in queue')).not.toBeNull()
    expect(screen.queryByText('1 movie left in queue')).toBeNull()
  })
})

// advanceQueue clears the log tail on success, but a failure settle left it in
// state -- and the panel renders whatever is there the moment the NEXT download
// starts, until that download's first status tick overwrites it.
describe("a failed attempt's logs cannot flash under the next download", () => {
  it('starting a new download shows no log lines from the failed one', async () => {
    renderPage()
    await flush(50)
    await startDownload()

    // A's download streams a log line, then fails.
    movieStatus = { inProgress: true, finished: false, error: null, logs: ['yt-dlp: fetching Movie A'] }
    await flush(1100)
    expect(screen.queryByText('yt-dlp: fetching Movie A')).not.toBeNull()
    movieStatus = { inProgress: false, finished: true, error: 'A failed', logs: ['yt-dlp: fetching Movie A'] }
    await flush(1100)
    expect(screen.queryByText('A failed')).not.toBeNull()

    // The user moves on to Movie B and downloads it. Before B's first status
    // tick, the panel must not replay A's log tail under B.
    movieStatus = { inProgress: true, finished: false, error: null, logs: [] }
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /movie b/i })) })
    await flush(50)
    await startDownload()

    expect(screen.queryByText(/downloading theme/i)).not.toBeNull()
    expect(screen.queryByText('yt-dlp: fetching Movie A')).toBeNull()
  })
})
