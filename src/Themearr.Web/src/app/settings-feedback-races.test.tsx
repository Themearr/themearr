import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

// Mirror settings-plex-url.test.tsx: the shared apiMock stubs every resource the
// page loads on mount, so these tests exercise only the feedback lifecycles.
vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')

// The page renders inside AppShell, which guards on useAuth() before rendering
// children at all, so the wrapper needs the auth context as well as a router.
function renderPage(ui: React.ReactElement) {
  return render(
    <MemoryRouter>
      <AuthProvider>{ui}</AuthProvider>
    </MemoryRouter>,
  )
}

// A promise the test settles by hand: the bugs under test are entirely about
// *when* a test/save response lands relative to the user's next action, so the
// request has to be held in flight and settled at a chosen moment -- same
// technique as queue-stale-error.test.tsx's deferredFailure().
function deferred<T>() {
  let resolve!: (v: T) => void
  let reject!: (e: Error) => void
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej })
  return { promise, resolve, reject }
}

// "Save"/"Test connection" repeat across the page (each Plex server card, the
// header's "Save changes"), so Radarr-form lookups anchor on the section heading
// and climb to the Section's root, the way settings-plex-url.test.tsx anchors on
// a server card via its name.
function sectionOf(title: string) {
  return screen.getByRole('heading', { name: title }).closest('div')!.parentElement as HTMLElement
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  // The fake-timer test advances far enough for the Sidebar's sync poll to
  // tick; an unstubbed status() returns undefined and the poll's .then throws.
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
  vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'k'.repeat(64) } as never)
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({
    selectedServers: [{ id: 'srv1', name: 'Tower', url: 'https://old.plex.direct:32400' }],
    selectedLibraries: {},
    pathMappings: [],
    libraryPaths: [],
    advanced: { maxSearchDirs: 20000, searchDepth: 4 },
    autoDownload: false,
    autoSync: false,
    lastAutoSyncAt: '',
  } as never)
  vi.mocked(api.plexApi.saveUrl).mockResolvedValue({
    selectedServers: [{ id: 'srv1', name: 'Tower', url: 'http://192.168.1.50:32400' }],
  } as never)
})

// The Radarr half of #26's minor fix: onChange cleared radarrTestResult but not
// radarrError/radarrSaved, so a stale error or "Saved ✓" lingered after the user
// started editing -- describing a URL/key no longer in the boxes.
describe('Library Source (Radarr) stale feedback', () => {
  it('clears a stale Radarr error and Saved flag when either field is edited', async () => {
    vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'radarr', url: 'http://localhost:7878', configured: false } as never)
    vi.mocked(api.radarrApi.test).mockRejectedValueOnce(new Error('Connection refused'))
    vi.mocked(api.radarrApi.save).mockResolvedValue({} as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    const urlInput = await screen.findByDisplayValue('http://localhost:7878')
    const section = sectionOf('Library Source')

    await userEvent.click(within(section).getByRole('button', { name: /test connection/i }))
    await within(section).findByText('Connection refused')

    // Editing the URL invalidates the stale error: it described the old URL.
    await userEvent.type(urlInput, 'x')
    expect(within(section).queryByText('Connection refused')).toBeNull()

    await userEvent.click(within(section).getByRole('button', { name: /^save/i }))
    await within(section).findByRole('button', { name: /saved/i })

    // Editing the API key invalidates the stale "Saved ✓" the same way.
    await userEvent.type(within(section).getByPlaceholderText('Radarr API key…'), 'k')
    expect(within(section).queryByRole('button', { name: /saved/i })).toBeNull()
  })
})

// The onChange clear (#26) empties the verdict boxes, but testPlexUrl/savePlexUrl
// wrote their per-server results unconditionally when the response landed -- so a
// slow response resurrected a verdict/error for the previously-typed URL.
describe('an in-flight Plex test/save cannot outlive a re-edit of that URL', () => {
  it('a test verdict landing after the URL was re-edited does not resurrect', async () => {
    const dl = deferred<{ ok: boolean; detail: string }>()
    vi.mocked(api.plexApi.test).mockReturnValue(dl.promise as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    const input = await screen.findByDisplayValue('https://old.plex.direct:32400')
    const card = screen.getByText('Tower').closest('div') as HTMLElement

    await userEvent.click(within(card).getByRole('button', { name: /test/i }))
    await waitFor(() => expect(api.plexApi.test).toHaveBeenCalledTimes(1))

    // Re-edit while the test is still in flight: its eventual verdict is about
    // the previously-typed URL, not this one.
    await userEvent.type(input, 'x')

    await act(async () => { dl.resolve({ ok: false, detail: 'The Plex server is unreachable.' }) })

    expect(within(card).queryByText(/unreachable/i)).toBeNull()
    // Suppressing the verdict must not wedge the button: the request IS over.
    await waitFor(() => expect(within(card).getByRole('button', { name: /test/i })).not.toBeDisabled())
  })

  it('a save failure landing after the URL was re-edited does not resurrect', async () => {
    const dl = deferred<never>()
    vi.mocked(api.plexApi.saveUrl).mockReturnValue(dl.promise as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    const input = await screen.findByDisplayValue('https://old.plex.direct:32400')
    const card = screen.getByText('Tower').closest('div') as HTMLElement

    await userEvent.click(within(card).getByRole('button', { name: /^save$/i }))
    await waitFor(() => expect(api.plexApi.saveUrl).toHaveBeenCalledTimes(1))

    await userEvent.type(input, 'x')

    await act(async () => { dl.reject(new Error('Could not reach the Plex server')) })

    expect(within(card).queryByText('Could not reach the Plex server')).toBeNull()
    await waitFor(() => expect(within(card).getByRole('button', { name: /^save$/i })).not.toBeDisabled())
  })
})

// savePlexUrl's 2s hide-timeout captured nothing about which save it belonged
// to, so a first save's timeout could fire inside a second save's confirmation
// window and hide its "Saved ✓" early. Fake timers because the bug is entirely
// about where the two 2s windows overlap; fireEvent + act rather than userEvent,
// as in queue-race.test.tsx: userEvent's own waiting doesn't see the fake clock.
describe("a prior save's hide-timeout cannot truncate a newer save's Saved ✓", () => {
  beforeEach(() => { vi.useFakeTimers() })
  afterEach(() => { vi.useRealTimers() })

  function flush(ms: number) {
    return act(async () => { await vi.advanceTimersByTimeAsync(ms) })
  }

  it("keeps the second save's Saved ✓ up for its own full window", async () => {
    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)
    await flush(50)

    const card = screen.getByText('Tower').closest('div') as HTMLElement

    // Save #1: confirmation shows, its hide-timeout is due at t+2000.
    await act(async () => { fireEvent.click(within(card).getByRole('button', { name: /^save/i })) })
    expect(within(card).getByRole('button', { name: /saved/i })).toBeInTheDocument()

    // Save #2 at t+1500 restarts the confirmation window.
    await flush(1500)
    await act(async () => { fireEvent.click(within(card).getByRole('button', { name: /^save/i })) })
    expect(within(card).getByRole('button', { name: /saved/i })).toBeInTheDocument()

    // t+2100: save #1's timeout fires. It must not hide save #2's confirmation,
    // which is only 600ms old.
    await flush(600)
    expect(within(card).queryByRole('button', { name: /saved/i })).not.toBeNull()

    // t+3600: save #2's own window is over -- the flag still clears.
    await flush(1500)
    expect(within(card).queryByRole('button', { name: /saved/i })).toBeNull()
  })
})
