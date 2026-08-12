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

// The Radarr form has the same in-flight races the Plex cards were just guarded
// against: responses and the save's reload wrote unconditionally, so they could
// land after the user's next action. Single-instance form, single stamp.
describe('an in-flight Radarr test/save cannot outlive a re-edit', () => {
  beforeEach(() => {
    vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'radarr', url: 'http://localhost:7878', configured: false } as never)
  })

  it('a test verdict landing after a field was re-edited does not resurrect', async () => {
    const dl = deferred<{ ok: boolean; detail: string }>()
    vi.mocked(api.radarrApi.test).mockReturnValue(dl.promise as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    const urlInput = await screen.findByDisplayValue('http://localhost:7878')
    const section = sectionOf('Library Source')

    await userEvent.click(within(section).getByRole('button', { name: /test connection/i }))
    await waitFor(() => expect(api.radarrApi.test).toHaveBeenCalledTimes(1))

    // Re-edit while the test is in flight: its verdict is about the old URL.
    await userEvent.type(urlInput, 'x')
    await act(async () => { dl.resolve({ ok: false, detail: 'Radarr is unreachable.' }) })

    expect(within(section).queryByText(/unreachable/i)).toBeNull()
    // Suppressing the verdict must not wedge the button: the request IS over.
    await waitFor(() => expect(within(section).getByRole('button', { name: /test connection/i })).not.toBeDisabled())
  })

  it("a save's reload landing after a re-edit does not clobber the newer text", async () => {
    const dl = deferred<object>()
    vi.mocked(api.radarrApi.save).mockReturnValue(dl.promise as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    const urlInput = await screen.findByDisplayValue('http://localhost:7878')
    const section = sectionOf('Library Source')

    await userEvent.click(within(section).getByRole('button', { name: /^save/i }))
    await waitFor(() => expect(api.radarrApi.save).toHaveBeenCalledTimes(1))

    // Keep typing while the POST is in flight; when it lands, the success
    // path's re-read of the stored config must not overwrite the box (nor may
    // a "Saved ✓" claim this superseded save for the text now in it).
    await userEvent.type(urlInput, 'x')
    await act(async () => { dl.resolve({}) })

    expect(urlInput).toHaveValue('http://localhost:7878x')
    expect(within(section).queryByRole('button', { name: /saved/i })).toBeNull()
  })

  it('a slow config re-read after a save does not clobber text typed meanwhile', async () => {
    const dl = deferred<{ source: string; url: string; configured: boolean }>()
    // First get() is the mount load; the deferred one is the post-save re-read.
    vi.mocked(api.radarrApi.get)
      .mockResolvedValueOnce({ source: 'radarr', url: 'http://localhost:7878', configured: false } as never)
      .mockReturnValue(dl.promise as never)
    vi.mocked(api.radarrApi.save).mockResolvedValue({} as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    const urlInput = await screen.findByDisplayValue('http://localhost:7878')
    const section = sectionOf('Library Source')

    await userEvent.click(within(section).getByRole('button', { name: /^save/i }))
    await waitFor(() => expect(api.radarrApi.get).toHaveBeenCalledTimes(2))

    // The POST is done; the re-read is still in flight when the user types.
    await userEvent.type(urlInput, 'x')
    await act(async () => { dl.resolve({ source: 'radarr', url: 'http://localhost:7878', configured: true }) })

    expect(urlInput).toHaveValue('http://localhost:7878x')
    expect(within(section).queryByRole('button', { name: /saved/i })).toBeNull()
  })
})

// The Radarr save's 2s hide-timeout has the same truncation as the Plex one:
// nothing tied it to the save it belonged to.
describe("a prior Radarr save's hide-timeout cannot truncate a newer save's Saved ✓", () => {
  beforeEach(() => {
    vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'radarr', url: 'http://localhost:7878', configured: false } as never)
    vi.mocked(api.radarrApi.save).mockResolvedValue({} as never)
    vi.useFakeTimers()
  })
  afterEach(() => { vi.useRealTimers() })

  function flush(ms: number) {
    return act(async () => { await vi.advanceTimersByTimeAsync(ms) })
  }

  it("keeps the second save's Saved ✓ up for its own full window", async () => {
    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)
    await flush(50)

    const section = sectionOf('Library Source')

    // Save #1: its hide-timeout is due at t+2000.
    await act(async () => { fireEvent.click(within(section).getByRole('button', { name: /^save/i })) })
    expect(within(section).getByRole('button', { name: /saved/i })).toBeInTheDocument()

    // Save #2 at t+1500 restarts the confirmation window.
    await flush(1500)
    await act(async () => { fireEvent.click(within(section).getByRole('button', { name: /^save/i })) })
    expect(within(section).getByRole('button', { name: /saved/i })).toBeInTheDocument()

    // t+2100: save #1's timeout fires -- save #2's confirmation must survive.
    await flush(600)
    expect(within(section).queryByRole('button', { name: /saved/i })).not.toBeNull()

    // t+3600: save #2's own window is over -- the flag still clears.
    await flush(1500)
    expect(within(section).queryByRole('button', { name: /saved/i })).toBeNull()
  })
})

// Concurrency-review finding on the stamp guard: a save superseded by an edit
// still persisted server-side. If its echo never reaches `settings`, any later
// whole-object save (the header's "Save changes", the library sections) posts
// selectedServers wholesale (SettingsController.cs:86) with the pre-save URL --
// silently reverting the save the server already applied.
describe("a superseded save's echo still lands in settings", () => {
  it('keeps the newer text in the box but posts the persisted URL on a later global save', async () => {
    const dl = deferred<{ selectedServers: { id: string; name: string; url: string }[] }>()
    vi.mocked(api.plexApi.saveUrl).mockReturnValue(dl.promise as never)
    vi.mocked(api.settingsApi.save).mockResolvedValue({} as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)

    const input = await screen.findByDisplayValue('https://old.plex.direct:32400')
    const card = screen.getByText('Tower').closest('div') as HTMLElement

    await userEvent.click(within(card).getByRole('button', { name: /^save$/i }))
    await waitFor(() => expect(api.plexApi.saveUrl).toHaveBeenCalledTimes(1))

    // Keep typing while the save is in flight, then let its response land: the
    // backend HAS persisted the normalised URL by now.
    await userEvent.type(input, 'x')
    await act(async () => {
      dl.resolve({ selectedServers: [{ id: 'srv1', name: 'Tower', url: 'http://192.168.1.50:32400' }] })
    })

    // The box keeps the newer typing -- the echo may not clobber it...
    expect(input).toHaveValue('https://old.plex.direct:32400x')

    // ...but a whole-object save must post the URL the server persisted, not
    // the pre-save one it would otherwise silently revert to.
    await userEvent.click(screen.getByRole('button', { name: /save changes/i }))
    await waitFor(() => expect(api.settingsApi.save).toHaveBeenCalledTimes(1))
    expect(vi.mocked(api.settingsApi.save).mock.calls[0][0]).toMatchObject({
      selectedServers: [{ id: 'srv1', url: 'http://192.168.1.50:32400' }],
    })
  })
})

// Concurrency-review finding: claiming a stamp without clearing the flag it
// governs wedges the flag. A test right after a save claims a new stamp, the
// save's hide-timeout defers to it, and nothing else ever clears "Saved ✓".
describe('a test right after a save cannot wedge Saved ✓ on', () => {
  beforeEach(() => { vi.useFakeTimers() })
  afterEach(() => { vi.useRealTimers() })

  function flush(ms: number) {
    return act(async () => { await vi.advanceTimersByTimeAsync(ms) })
  }

  it('the confirmation still goes away when a test supersedes the save', async () => {
    vi.mocked(api.plexApi.test).mockResolvedValue({ ok: true, detail: 'Reached the Plex server.' } as never)

    const { default: SettingsPage } = await import('@/app/settings/page')
    renderPage(<SettingsPage />)
    await flush(50)

    const card = screen.getByText('Tower').closest('div') as HTMLElement

    await act(async () => { fireEvent.click(within(card).getByRole('button', { name: /^save/i })) })
    expect(within(card).getByRole('button', { name: /saved/i })).toBeInTheDocument()

    // t+1000: a test on the same server supersedes the save's stamp.
    await flush(1000)
    await act(async () => { fireEvent.click(within(card).getByRole('button', { name: /test connection/i })) })
    expect(within(card).getByText(/reached the plex server/i)).toBeInTheDocument()

    // Long past every 2s window the confirmation must be gone -- the save's
    // superseded timeout defers, so something else has to have cleared it.
    await flush(5000)
    expect(within(card).queryByRole('button', { name: /saved/i })).toBeNull()
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
