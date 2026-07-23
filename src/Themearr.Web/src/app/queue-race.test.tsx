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

// Everything here runs on fake timers, because the bug under test is entirely
// about the relative timing of a 1s poll and a slower-than-1s response. Driving
// it with `fireEvent` + `act` rather than `userEvent` keeps the two compatible:
// userEvent's own waiting doesn't see vitest's fake clock.
function flush(ms: number) {
  return act(async () => { await vi.advanceTimersByTimeAsync(ms) })
}

const movie = (id: string, title: string, year: number) => ({
  id, source: 'plex', sourceRef: `r-${id}`, title, year,
  sourcePath: null, folderName: title, status: 'pending', posterUrl: null,
})

beforeEach(() => {
  vi.clearAllMocks()
  vi.useFakeTimers()
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
  vi.mocked(api.moviesApi.download).mockResolvedValue({ started: true, movieId: 'a' } as never)
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

describe('the download-status poll cannot skip a movie', () => {
  it('does not advance twice when a status response outlives the poll interval', async () => {
    // Every status call reports "finished", and takes 1.5s -- longer than the
    // 1s poll interval, so a second callback fires while the first is still
    // awaiting. Both then observe `finished`.
    vi.mocked(api.moviesApi.downloadStatus).mockImplementation(
      () => new Promise(resolve => {
        setTimeout(() => resolve({ inProgress: false, finished: true, error: null, logs: [] }), 1500)
      }) as never,
    )

    const { default: QueuePage } = await import('@/app/queue/page')
    renderPage(<QueuePage />)

    await flush(50)
    await startDownload()

    // t+1000 poll #1 starts, t+2000 poll #2 starts, t+2500 poll #1 sees
    // "finished" and advances, t+3500 poll #2 resolves -- and must not advance
    // a second time.
    await flush(4000)

    // The queue advanced by exactly one: A is done, B is up, C is still behind it.
    expect(screen.queryByText('2 movies left in queue')).not.toBeNull()
    expect(screen.queryByText('1 movie left in queue')).toBeNull()
    // ...and the page never went looking for a theme for C, which is what a
    // silently skipped B looks like from the outside.
    expect(vi.mocked(api.moviesApi.search).mock.calls.map(c => c[0])).not.toContain('c')
  })

  it('still advances once when the status response comes back inside the interval', async () => {
    vi.mocked(api.moviesApi.downloadStatus).mockResolvedValue(
      { inProgress: false, finished: true, error: null, logs: [] } as never,
    )

    const { default: QueuePage } = await import('@/app/queue/page')
    renderPage(<QueuePage />)

    await flush(50)
    await startDownload()
    await flush(1500)

    expect(screen.queryByText('2 movies left in queue')).not.toBeNull()
    expect(screen.queryByText('3 movies left in queue')).toBeNull()
  })
})

describe('the download-status poll cannot be wedged by a hung request', () => {
  it('recovers -- polling resumes and controls re-enable -- when a request never settles', async () => {
    // A dropped connection or a server stuck mid-request never resolves *and*
    // never rejects on its own -- unlike every other failure mode the existing
    // tests cover. The only thing that ever settles it is the production
    // code's own AbortController firing, exactly like a real `fetch` handed
    // an AbortSignal: it stays pending until aborted, then rejects. A second,
    // independent call (a fresh connection after the first was abandoned)
    // succeeds normally, which is what proves polling actually resumed rather
    // than the queue just sitting there.
    let calls = 0
    vi.mocked(api.moviesApi.downloadStatus).mockImplementation(
      ((_movieId: string, init?: RequestInit) => {
        calls++
        if (calls === 1) {
          return new Promise((_resolve, reject) => {
            init?.signal?.addEventListener('abort', () =>
              reject(new DOMException('The operation was aborted', 'AbortError')))
          })
        }
        return Promise.resolve({ inProgress: false, finished: true, error: null, logs: [] })
      }) as never,
    )

    const { default: QueuePage } = await import('@/app/queue/page')
    renderPage(<QueuePage />)

    await flush(50)
    await startDownload()

    // While the first request is hung, the queue is stuck on Movie A and its
    // only in-app escapes -- Skip and Ignore -- are disabled.
    await flush(2000)
    expect(calls).toBe(1)
    expect(screen.getByRole('button', { name: /^skip$/i })).toBeDisabled()
    expect(screen.getByRole('button', { name: /^ignore$/i })).toBeDisabled()

    // Past the timeout, the hung request is aborted and rejects, the
    // in-flight flag clears, and a later poll tick issues a fresh request
    // that succeeds.
    await flush(9000)

    // The queue recovered on its own -- no reload needed: a second request
    // went out, the queue advanced off Movie A, and the controls are usable
    // again.
    expect(calls).toBeGreaterThan(1)
    expect(screen.queryByText('2 movies left in queue')).not.toBeNull()
    expect(screen.getByRole('button', { name: /^skip$/i })).not.toBeDisabled()
    expect(screen.getByRole('button', { name: /^ignore$/i })).not.toBeDisabled()
  })
})
