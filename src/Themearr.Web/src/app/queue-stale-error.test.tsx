import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
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

// A promise the test rejects by hand: the bug under test (#43) is entirely about
// *when* a download's rejection lands relative to the user browsing the queue,
// so the download has to be held in flight while the test navigates, then
// failed at a chosen moment.
function deferredFailure() {
  let reject!: (e: Error) => void
  const promise = new Promise((_res, rej) => { reject = rej })
  return { promise, reject }
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
  vi.mocked(api.moviesApi.search).mockResolvedValue({
    movie: {},
    results: [{ videoId: 'v1', title: 'A theme', thumbnail: null, duration: null, channel: 'ch', score: 1, bestMatch: false }],
  } as never)
  // The status poll runs on real timers here; a permanently-pending status keeps
  // its ticks from ever advancing or erroring under the assertions.
  vi.mocked(api.moviesApi.downloadStatus).mockResolvedValue(
    { inProgress: true, finished: false, error: null, logs: [] } as never,
  )
  vi.mocked(api.showsApi.downloadStatus).mockResolvedValue(
    { inProgress: true, finished: false, error: null, logs: [] } as never,
  )
})

// The "Up next" rows and the media toggle are deliberately NOT disabled while a
// download runs -- browsing the queue during one is fine. The price is that a
// download's rejection can arrive with a different item on the card, and its
// error must not render there: a banner under the wrong title reads as that
// title having failed.
describe('a stale download rejection cannot label the item now on screen (#43)', () => {
  it('a rejected download does not put its error under the movie the user browsed to', async () => {
    const user = userEvent.setup()
    const dl = deferredFailure()
    vi.mocked(api.moviesApi.download).mockReturnValue(dl.promise as never)

    renderPage()
    await screen.findByText('3 movies left in queue')
    // Wait for the search results: until they render, the only "Download"
    // button is the manual-URL form's disabled one.
    await screen.findByText('A theme')

    // Start Movie A's download from its first search result. (The manual-URL
    // form has a second "Download" button, disabled while its input is empty.)
    await user.click(screen.getAllByRole('button', { name: /^download$/i })[0])
    await waitFor(() => expect(api.moviesApi.download).toHaveBeenCalledWith('a', 'v1'))

    // Browse to Movie B while A's download is still in flight.
    await user.click(screen.getByRole('button', { name: /movie b/i }))
    await screen.findByText('2 movies left in queue')

    // A's download now fails. Its error belongs to A, and A is not on screen.
    await act(async () => { dl.reject(new Error('yt-dlp exploded on Movie A')) })

    expect(screen.queryByText(/yt-dlp exploded on Movie A/i)).toBeNull()
    // Suppressing the banner must not cost the recovery: the download is over,
    // so Skip re-enables rather than staying wedged behind `downloading`.
    await waitFor(() => expect(screen.getByRole('button', { name: /^skip$/i })).not.toBeDisabled())
  })

  it('a rejected manual-URL download does not put its error under the movie the user browsed to', async () => {
    const user = userEvent.setup()
    const dl = deferredFailure()
    vi.mocked(api.moviesApi.downloadUrl).mockReturnValue(dl.promise as never)

    renderPage()
    await screen.findByText('3 movies left in queue')
    await screen.findByText('A theme')

    await user.type(screen.getByPlaceholderText(/youtube\.com\/watch/i), 'https://www.youtube.com/watch?v=x')
    // The manual-URL form's Download button is the last one on the page.
    await user.click(screen.getAllByRole('button', { name: /^download$/i }).at(-1)!)
    await waitFor(() => expect(api.moviesApi.downloadUrl).toHaveBeenCalledTimes(1))

    await user.click(screen.getByRole('button', { name: /movie b/i }))
    await screen.findByText('2 movies left in queue')

    await act(async () => { dl.reject(new Error('bad URL for Movie A')) })

    expect(screen.queryByText(/bad URL for Movie A/i)).toBeNull()
    await waitFor(() => expect(screen.getByRole('button', { name: /^skip$/i })).not.toBeDisabled())
  })

  it("auto mode's failed download does not land its error under the movie the user browsed to", async () => {
    const user = userEvent.setup()
    const dl = deferredFailure()
    vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: true } as never)
    vi.mocked(api.settingsApi.save).mockResolvedValue({} as never)
    vi.mocked(api.moviesApi.autoDownload).mockImplementation(
      ((id: string) => (id === 'a' ? dl.promise : new Promise(() => {}))) as never,
    )

    renderPage()
    // Auto mode starts Movie A's download on its own.
    await waitFor(() => expect(api.moviesApi.autoDownload).toHaveBeenCalledWith('a'))

    await user.click(screen.getByRole('button', { name: /movie b/i }))
    await screen.findByText('2 movies left in queue')

    // Turn auto mode off before the rejection lands. Without this, the auto
    // effect restarts on Movie B the moment `downloading` clears, and its own
    // setError('') wipes the stale banner inside the same act() flush -- the
    // commit collapse #43's testing note warns about. With auto off there is
    // no converging writer left: whatever the catch handler wrote IS the
    // settled DOM, so the intermediate state is directly observable.
    const toggle = screen.getByRole('button', { name: /auto/i })
    await user.click(toggle)
    await waitFor(() => expect(api.settingsApi.save).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(toggle).not.toBeDisabled())

    await act(async () => { dl.reject(new Error('auto-download failed on Movie A')) })

    expect(screen.queryByText(/auto-download failed on Movie A/i)).toBeNull()
    await waitFor(() => expect(screen.getByRole('button', { name: /^skip$/i })).not.toBeDisabled())
  })

  it("a movie download's failure does not land under a show that shares its id", async () => {
    const user = userEvent.setup()
    const dl = deferredFailure()
    // Ids come from different tables, so a movie and a show sharing '7' is
    // possible -- the identity gate must key on media + id, not the bare id.
    vi.mocked(api.moviesApi.list).mockResolvedValue([movie('7', 'Movie Seven', 2001)] as never)
    vi.mocked(api.showsApi.list).mockResolvedValue([movie('7', 'Show Seven', 2002)] as never)
    vi.mocked(api.showsApi.search).mockResolvedValue({ results: [] } as never)
    vi.mocked(api.moviesApi.download).mockReturnValue(dl.promise as never)

    renderPage()
    await screen.findByText('1 movie left in queue')
    await screen.findByText('A theme')
    await user.click(screen.getAllByRole('button', { name: /^download$/i })[0])
    await waitFor(() => expect(api.moviesApi.download).toHaveBeenCalledWith('7', 'v1'))

    // The media toggle is not disabled during a download either.
    await user.click(screen.getByRole('button', { name: /^Shows$/ }))
    await screen.findByText('1 show left in queue')

    // The movie's failure arrives while show 7 is on the card.
    await act(async () => { dl.reject(new Error('movie seven download failed')) })

    expect(screen.queryByText(/movie seven download failed/i)).toBeNull()
    await waitFor(() => expect(screen.getByRole('button', { name: /^skip$/i })).not.toBeDisabled())
  })

  it('a rejected download still shows its error when its movie is the one on screen', async () => {
    const user = userEvent.setup()
    const dl = deferredFailure()
    vi.mocked(api.moviesApi.download).mockReturnValue(dl.promise as never)

    renderPage()
    await screen.findByText('3 movies left in queue')
    await screen.findByText('A theme')
    await user.click(screen.getAllByRole('button', { name: /^download$/i })[0])
    await waitFor(() => expect(api.moviesApi.download).toHaveBeenCalledTimes(1))

    await act(async () => { dl.reject(new Error('yt-dlp exploded on Movie A')) })

    // Still on Movie A, so this failure is exactly where it belongs -- the
    // identity gate must not suppress a legitimately-placed error.
    expect(screen.queryByText(/yt-dlp exploded on Movie A/i)).not.toBeNull()
    expect(screen.getByRole('button', { name: /^skip$/i })).not.toBeDisabled()
  })
})

// The ownership half of the gate needs its own clock: a second attempt on the
// same item only becomes possible after the status poll's lost-contact path
// (three consecutive ~8s hung checks) hands control back, which only fake
// timers can reach. fireEvent + act rather than userEvent, as in
// queue-race.test.tsx: userEvent's own waiting doesn't see vitest's fake clock.
describe('a stale rejection cannot tear down a newer download of the same item (#43)', () => {
  beforeEach(() => { vi.useFakeTimers() })
  afterEach(() => { vi.useRealTimers() })

  function flush(ms: number) {
    return act(async () => { await vi.advanceTimersByTimeAsync(ms) })
  }

  it('keeps a retry of the same movie alive when the first attempt rejects late', async () => {
    // Every status check hangs until the page's own AbortController fires, so
    // the poll fails three times and gives up -- re-enabling the controls
    // while attempt #1's download request is still pending.
    vi.mocked(api.moviesApi.downloadStatus).mockImplementation(
      ((_movieId: string, init?: RequestInit) =>
        new Promise((_resolve, reject) => {
          init?.signal?.addEventListener('abort', () =>
            reject(new DOMException('The operation was aborted', 'AbortError')))
        })) as never,
    )
    const first = deferredFailure()
    vi.mocked(api.moviesApi.download)
      .mockReturnValueOnce(first.promise as never)
      // Attempt #2 stays in flight for the rest of the test.
      .mockReturnValue(new Promise(() => {}) as never)

    renderPage()
    await flush(50)

    // Attempt #1 on Movie A.
    await act(async () => { fireEvent.click(screen.getAllByRole('button', { name: /^download$/i })[0]) })
    expect(api.moviesApi.download).toHaveBeenCalledTimes(1)

    // The poll gives up and hands control back; the hung request is NOT over.
    await flush(60000)
    expect(screen.queryByText(/lost contact/i)).not.toBeNull()
    expect(screen.getByRole('button', { name: /^skip$/i })).not.toBeDisabled()

    // Attempt #2 on the same movie.
    await act(async () => { fireEvent.click(screen.getAllByRole('button', { name: /^download$/i })[0]) })
    expect(api.moviesApi.download).toHaveBeenCalledTimes(2)
    expect(screen.getByRole('button', { name: /^skip$/i })).toBeDisabled()

    // Attempt #1's request finally rejects. Movie A is still on screen, so
    // only per-attempt ownership -- not placement, and not a bare-id check,
    // which #2 re-satisfied -- can tell this rejection from #2's outcome.
    await act(async () => { first.reject(new Error('first attempt failed')) })

    // Attempt #2 must be untouched: still downloading, no stale banner.
    expect(screen.queryByText(/first attempt failed/i)).toBeNull()
    expect(screen.getByRole('button', { name: /^skip$/i })).toBeDisabled()
    expect(screen.queryByText(/downloading theme/i)).not.toBeNull()
  })
})
