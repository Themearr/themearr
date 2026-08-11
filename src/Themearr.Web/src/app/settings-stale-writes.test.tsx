import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

// Sweep companion to settings-feedback-races.test.tsx: the remaining places on
// the settings page where a response or timeout landing late overwrites newer
// state. Shared apiMock as everywhere else, so mounts are quiet.
vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')

function renderPage(ui: React.ReactElement) {
  return render(
    <MemoryRouter>
      <AuthProvider>{ui}</AuthProvider>
    </MemoryRouter>,
  )
}

// Hand-settled promise, as in queue-stale-error.test.tsx: these bugs are about
// *when* a response lands relative to the user's next action.
function deferred<T>() {
  let resolve!: (v: T) => void
  let reject!: (e: Error) => void
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej })
  return { promise, resolve, reject }
}

// Scopes queries to one settings Section -- several button labels ("Remove",
// "Test connection", "Save") repeat across the page. Section renders
// <div><div><h2>{title}</h2>…</div>{children}</div>, as in
// settings-movie-libraries.test.tsx's helper.
function sectionOf(title: string) {
  return within(screen.getByRole('heading', { name: title }).parentElement!.parentElement!)
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
  vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'k'.repeat(64) } as never)
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({
    selectedServers: [{ id: 'srv1', name: 'Tower', url: 'https://old.plex.direct:32400' }],
    selectedLibraries: {},
    selectedShowLibraries: {},
    pathMappings: [],
    libraryPaths: [],
    advanced: { maxSearchDirs: 20000, searchDepth: 4 },
    autoDownload: false,
    autoSync: false,
    lastAutoSyncAt: '',
  } as never)
  vi.mocked(api.settingsApi.plexLibraries).mockResolvedValue({
    libraries: { srv1: [
      { key: '1', title: 'Films', type: 'movie' },
      { key: '3', title: 'TV Shows', type: 'show' },
    ] },
  } as never)
  vi.mocked(api.settingsApi.save).mockResolvedValue({} as never)
})

// saveMovieLibraries/saveShowLibraries wrote `next` -- the whole settings
// object snapshotted before the await -- back into state when the POST landed,
// reverting any other edit made while it was in flight.
describe("a library save's snapshot cannot revert edits made while it was in flight", () => {
  it('a movie-library save landing late does not revert a queue toggle', async () => {
    const dl = deferred<object>()
    vi.mocked(api.settingsApi.save).mockReturnValue(dl.promise as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)
    await screen.findByLabelText('Films')

    await userEvent.click(screen.getByRole('button', { name: /save movie libraries/i }))
    await waitFor(() => expect(api.settingsApi.save).toHaveBeenCalledTimes(1))

    // Toggle Auto-download while the save is in flight.
    await userEvent.click(screen.getAllByRole('switch')[0])
    expect(screen.getAllByRole('switch')[0]).toHaveAttribute('aria-checked', 'true')

    await act(async () => { dl.resolve({}) })

    // The save's pre-await snapshot has autoDownload=false; writing it back
    // wholesale would silently flip the toggle off again.
    expect(screen.getAllByRole('switch')[0]).toHaveAttribute('aria-checked', 'true')
  })

  it('a show-library save landing late does not revert a queue toggle', async () => {
    const dl = deferred<object>()
    vi.mocked(api.settingsApi.save).mockReturnValue(dl.promise as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)
    await screen.findByLabelText('TV Shows')

    await userEvent.click(screen.getByRole('button', { name: /save show libraries/i }))
    await waitFor(() => expect(api.settingsApi.save).toHaveBeenCalledTimes(1))

    await userEvent.click(screen.getAllByRole('switch')[0])
    await act(async () => { dl.resolve({}) })

    expect(screen.getAllByRole('switch')[0]).toHaveAttribute('aria-checked', 'true')
  })
})

// Ticking a library clears the section's saved flag (the sync prompt) on
// purpose -- the prompt describes a selection that is no longer current. A save
// still in flight set the flag back when it landed, resurrecting the prompt.
describe('a library save landing after a re-tick cannot resurrect the sync prompt', () => {
  it("a movie-library save's late success does not re-show the prompt", async () => {
    const dl = deferred<object>()
    vi.mocked(api.settingsApi.save).mockReturnValue(dl.promise as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)
    await screen.findByLabelText('Films')

    await userEvent.click(screen.getByRole('button', { name: /save movie libraries/i }))
    await waitFor(() => expect(api.settingsApi.save).toHaveBeenCalledTimes(1))

    // Re-tick while the save is in flight: the selection being saved is no
    // longer the selection on screen.
    await userEvent.click(screen.getByLabelText('Films'))
    await act(async () => { dl.resolve({}) })

    expect(screen.queryByText(/run a sync to apply/i)).toBeNull()
  })

  it("a show-library save's late success does not re-show the prompt", async () => {
    const dl = deferred<object>()
    vi.mocked(api.settingsApi.save).mockReturnValue(dl.promise as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)
    await screen.findByLabelText('TV Shows')

    await userEvent.click(screen.getByRole('button', { name: /save show libraries/i }))
    await waitFor(() => expect(api.settingsApi.save).toHaveBeenCalledTimes(1))

    await userEvent.click(screen.getByLabelText('TV Shows'))
    await act(async () => { dl.resolve({}) })

    expect(screen.queryByText(/run a sync to apply/i)).toBeNull()
  })
})

// Replace and Remove are separate buttons that can be in flight together; each
// wrote rapidApiOk unconditionally, so the slower response decided the panel's
// state regardless of which action came last.
describe('a slow RapidAPI Replace cannot outvote a later Remove', () => {
  it('the panel stays unconfigured when the earlier Replace resolves last', async () => {
    vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: true } as never)
    vi.mocked(api.rapidApiApi.remove).mockResolvedValue({} as never)
    const dl = deferred<object>()
    vi.mocked(api.rapidApiApi.save).mockReturnValue(dl.promise as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)
    await screen.findByText('API key configured')

    const rapid = sectionOf('RapidAPI Key')
    await userEvent.type(screen.getByPlaceholderText('New RapidAPI key…'), 'newkey')
    await userEvent.type(screen.getByPlaceholderText('RapidAPI username…'), 'user')
    await userEvent.click(rapid.getByRole('button', { name: /^replace$/i }))
    await waitFor(() => expect(api.rapidApiApi.save).toHaveBeenCalledTimes(1))

    // Remove wins the user's intent: it was clicked after Replace.
    await userEvent.click(rapid.getByRole('button', { name: /^remove$/i }))
    await waitFor(() => expect(screen.queryByText('API key configured')).toBeNull())

    // The old Replace's success lands last -- it must not re-claim the panel.
    await act(async () => { dl.resolve({}) })
    expect(screen.queryByText('API key configured')).toBeNull()
  })
})

// The RapidAPI save's success path empties the key/username boxes. Typed input
// is the one thing a late response must never eat (the rule the Radarr
// config-reload fix follows): an edit made while the POST is in flight owns
// the boxes.
describe('a RapidAPI save landing after a re-edit cannot eat the newer typing', () => {
  it('keeps text typed while the save was in flight', async () => {
    const dl = deferred<object>()
    vi.mocked(api.rapidApiApi.save).mockReturnValue(dl.promise as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    const keyInput = await screen.findByPlaceholderText('RapidAPI key…')
    await userEvent.type(keyInput, 'first-key')
    await userEvent.type(screen.getByPlaceholderText('RapidAPI username…'), 'user')

    const rapid = sectionOf('RapidAPI Key')
    await userEvent.click(rapid.getByRole('button', { name: /^save$/i }))
    await waitFor(() => expect(api.rapidApiApi.save).toHaveBeenCalledTimes(1))

    // A correction typed while the POST is in flight.
    await userEvent.type(keyInput, '-oops')
    await act(async () => { dl.resolve({}) })

    // Queried from the document, not via the held element: the pre-fix flip to
    // the "configured" branch unmounts the input, and a detached node would
    // still report its old value. What matters is that the typing is on screen.
    expect(screen.getByDisplayValue('first-key-oops')).toBeInTheDocument()
  })
})

// The update poll writes st.logs wholesale per tick. A slow tick resolving
// after the tick that observed `finished` used to land its older logs on the
// closed modal -- and with the interval gone, nothing ever healed them.
describe('a stale update-status tick cannot truncate the finished log', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    // jsdom doesn't implement scrollIntoView, which the modal's auto-scroll
    // effect calls on every log change.
    Element.prototype.scrollIntoView = vi.fn()
  })
  afterEach(() => {
    vi.useRealTimers()
    Reflect.deleteProperty(Element.prototype, 'scrollIntoView')
  })

  function flush(ms: number) {
    return act(async () => { await vi.advanceTimersByTimeAsync(ms) })
  }

  it('keeps the final logs when an earlier slow tick resolves last', async () => {
    vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v2', updateAvailable: true } as never)
    vi.mocked(api.versionApi.update).mockResolvedValue({} as never)
    const slowTick = deferred<{ inProgress: boolean; finished: boolean; error: string | null; logs: string[] }>()
    vi.mocked(api.versionApi.updateStatus)
      .mockReturnValueOnce(slowTick.promise as never)
      .mockResolvedValue({ inProgress: false, finished: true, error: null, logs: ['line-a', 'line-b'] } as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)
    await flush(50)

    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /update now/i })) })

    // t+1000: tick #1 hangs. t+2000: tick #2 sees `finished` and closes out
    // the update with the full log.
    await flush(2000)
    expect(screen.getByText('line-b')).toBeInTheDocument()

    // Tick #1 finally resolves with the older, shorter log. The poll is over;
    // if this write lands, nothing will ever restore line-b.
    await act(async () => {
      slowTick.resolve({ inProgress: true, finished: false, error: null, logs: ['line-a'] })
    })

    expect(screen.getByText('line-b')).toBeInTheDocument()
    expect(screen.getByText(/update complete/i)).toBeInTheDocument()
  })
})

// Switching the library source invalidates the Radarr form's feedback the same
// way editing a field does -- including a response still in flight, whose error
// would otherwise surface under the *Plex* branch's Save button.
describe('switching library source drops in-flight Radarr feedback', () => {
  it("an in-flight test's failure does not surface after switching to Plex", async () => {
    vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'radarr', url: 'http://localhost:7878', configured: false } as never)
    const dl = deferred<{ ok: boolean; detail: string }>()
    vi.mocked(api.radarrApi.test).mockReturnValue(dl.promise as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)
    await screen.findByDisplayValue('http://localhost:7878')

    const source = sectionOf('Library Source')
    await userEvent.click(source.getByRole('button', { name: /test connection/i }))
    await waitFor(() => expect(api.radarrApi.test).toHaveBeenCalledTimes(1))

    await userEvent.click(source.getByRole('button', { name: /^plex$/i }))
    await act(async () => { dl.reject(new Error('Radarr connection refused')) })

    expect(screen.queryByText('Radarr connection refused')).toBeNull()
  })
})

// The standalone 2s ✓ flags (header Save, Regenerate, the Copy buttons) had
// naked hide-timeouts: retriggering inside the window let the earlier timeout
// truncate the newer confirmation. Fake timers + fireEvent as in
// queue-race.test.tsx; userEvent's own waiting doesn't see the fake clock.
describe("an earlier trigger's 2s timeout cannot truncate a re-triggered ✓ flag", () => {
  beforeEach(() => { vi.useFakeTimers() })
  afterEach(() => { vi.useRealTimers() })

  function flush(ms: number) {
    return act(async () => { await vi.advanceTimersByTimeAsync(ms) })
  }

  it("the header's Saved ✓ survives a second save inside the first window", async () => {
    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)
    await flush(50)

    const headerSave = screen.getByRole('button', { name: /save changes/i })
    await act(async () => { fireEvent.click(headerSave) })
    expect(headerSave).toHaveTextContent('Saved ✓')

    await flush(1500)
    await act(async () => { fireEvent.click(headerSave) })

    // t+2100: the first save's timeout fires -- the second's window survives.
    await flush(600)
    expect(headerSave).toHaveTextContent('Saved ✓')

    // t+3600: the second save's own window is over.
    await flush(1500)
    expect(headerSave).toHaveTextContent('Save changes')
  })

  it('Regenerated ✓ survives a second regeneration inside the first window', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    vi.mocked(api.apiKeyApi.regenerate).mockResolvedValue({ key: 'n'.repeat(64) } as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)
    await flush(50)

    const btn = screen.getByRole('button', { name: /^regenerate$/i })
    await act(async () => { fireEvent.click(btn) })
    expect(btn).toHaveTextContent('Regenerated ✓')

    await flush(1500)
    await act(async () => { fireEvent.click(btn) })

    await flush(600)
    expect(btn).toHaveTextContent('Regenerated ✓')

    await flush(1500)
    expect(btn).toHaveTextContent('Regenerate')
  })

  it('Copied ✓ survives a second copy inside the first window', async () => {
    // jsdom has no clipboard; give it the secure-context happy path so the
    // copy succeeds and the flag lifecycle under test actually runs.
    Object.defineProperty(window, 'isSecureContext', { value: true, configurable: true })
    Object.defineProperty(window.navigator, 'clipboard', {
      value: { writeText: vi.fn().mockResolvedValue(undefined) },
      configurable: true,
    })
    try {
      const { default: SettingsPage } = await import('@/app/settings/page')
      renderPage(<SettingsPage />)
      await flush(50)

      const copyBtn = screen.getAllByRole('button', { name: /^copy$/i })[0]
      await act(async () => { fireEvent.click(copyBtn) })
      expect(copyBtn).toHaveTextContent('Copied ✓')

      await flush(1500)
      await act(async () => { fireEvent.click(copyBtn) })

      await flush(600)
      expect(copyBtn).toHaveTextContent('Copied ✓')

      await flush(1500)
      expect(copyBtn).toHaveTextContent('Copy')
    } finally {
      Reflect.deleteProperty(window.navigator, 'clipboard')
      Reflect.deleteProperty(window, 'isSecureContext')
    }
  })
})
