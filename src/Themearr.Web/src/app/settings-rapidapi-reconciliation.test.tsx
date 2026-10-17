import { act, fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AuthProvider } from '@/lib/auth'
vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
const api = await import('@/lib/api')
const { default: SettingsPage } = await import('./settings/page')
function deferred<T>() { let resolve!: (v: T) => void; let reject!: (e: Error) => void; const promise = new Promise<T>((r, j) => { resolve = r; reject = j }); return { promise, resolve, reject } }
async function tick(ms = 0) { await act(async () => { await vi.advanceTimersByTimeAsync(ms) }) }
function rapid() { return within(screen.getByRole('heading', { name: 'RapidAPI Key' }).parentElement!.parentElement!) }
function fields(key = 'new-key', user = 'user') {
  fireEvent.change(rapid().getByPlaceholderText(/^(New )?RapidAPI key…$/), { target: { value: key } })
  fireEvent.change(rapid().getByPlaceholderText('RapidAPI username…'), { target: { value: user } })
}
async function replace() { fields(); await act(async () => { fireEvent.click(rapid().getByRole('button', { name: /^(replace|save)$/i })) }) }
async function remove() { await act(async () => { fireEvent.click(rapid().getByRole('button', { name: /^remove$/i })) }) }
function open() { return render(<MemoryRouter><AuthProvider><SettingsPage /></AuthProvider></MemoryRouter>) }
beforeEach(() => {
  vi.clearAllMocks(); vi.useFakeTimers()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
  vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'k'.repeat(64) } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({ selectedServers: [], selectedLibraries: {}, selectedShowLibraries: {}, pathMappings: [], libraryPaths: [], advanced: { maxSearchDirs: 20000, searchDepth: 4 }, autoDownload: false, autoSync: false, lastAutoSyncAt: '' } as never)
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: true } as never)
  vi.mocked(api.rapidApiApi.save).mockResolvedValue({} as never)
  vi.mocked(api.rapidApiApi.remove).mockResolvedValue({} as never)
})
afterEach(() => vi.useRealTimers())

for (const configured of [true, false]) {
  it(`preserves immediate Remove feedback then reconciles server order to configured=${configured}`, async () => {
    const save = deferred<object>(); const del = deferred<object>()
    vi.mocked(api.rapidApiApi.save).mockReturnValue(save.promise as never); vi.mocked(api.rapidApiApi.remove).mockReturnValue(del.promise as never)
    open(); await tick(); await replace(); await remove()
    await act(async () => { del.resolve({}) })
    expect(rapid().queryByText('API key configured')).not.toBeInTheDocument()
    await tick(1000); expect(api.rapidApiApi.status).toHaveBeenCalledTimes(1)
    vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured } as never)
    await act(async () => { save.resolve({}) })
    expect(rapid().queryByText('API key configured')).not.toBeInTheDocument()
    await tick(249); expect(api.rapidApiApi.status).toHaveBeenCalledTimes(1)
    await tick(1); expect(api.rapidApiApi.status).toHaveBeenCalledTimes(2)
    expect(Boolean(rapid().queryByText('API key configured'))).toBe(configured)
    if (configured) expect(rapid().getByText(/differs from your last action/i)).toBeInTheDocument()
  })
}

it('guards an initial status result landing after a newer removal', async () => {
  const status = deferred<{ configured: boolean }>(); vi.mocked(api.rapidApiApi.status).mockReturnValueOnce(status.promise)
  open(); await tick()
  // Fill credentials while the initial check is pending only after another confirmed read.
  // Retry status owns the latest read; the deferred mount cannot overwrite it.
  await tick(8100)
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: true })
  await act(async () => { fireEvent.click(rapid().getByRole('button', { name: /^retry$/i })) })
  await remove(); await act(async () => { status.resolve({ configured: true }) })
  expect(rapid().queryByText('API key configured')).not.toBeInTheDocument()
})

it('preserves edits while authoritative status catches up', async () => {
  const save = deferred<object>(); vi.mocked(api.rapidApiApi.save).mockReturnValue(save.promise as never)
  open(); await tick(); await replace(); fields('newer-typing', 'newer-user')
  await act(async () => { save.resolve({}) }); await tick(250)
  expect(rapid().getByPlaceholderText('New RapidAPI key…')).toHaveValue('newer-typing')
  expect(rapid().getByPlaceholderText('RapidAPI username…')).toHaveValue('newer-user')
  expect(api.rapidApiApi.status).toHaveBeenCalledTimes(2)
})

it('invalidates a pending reconciliation when another mutation starts', async () => {
  const read = deferred<{ configured: boolean }>()
  open(); await tick(); await replace()
  vi.mocked(api.rapidApiApi.status).mockReturnValueOnce(read.promise)
  await tick(250); await remove(); await act(async () => { read.resolve({ configured: true }) })
  expect(rapid().queryByText('API key configured')).not.toBeInTheDocument()
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false })
  await tick(250); expect(rapid().queryByText('API key configured')).not.toBeInTheDocument()
})

it('cancels a scheduled reconciliation after unmount', async () => {
  const { unmount } = open(); await tick(); await replace(); unmount(); await tick(300)
  expect(api.rapidApiApi.status).toHaveBeenCalledTimes(1)
})

it('reconciles an earlier Remove that commits after a later Replace', async () => {
  const del = deferred<object>(); const save = deferred<object>()
  vi.mocked(api.rapidApiApi.remove).mockReturnValue(del.promise as never); vi.mocked(api.rapidApiApi.save).mockReturnValue(save.promise as never)
  open(); await tick(); await remove(); await replace()
  await act(async () => { save.resolve({}) }); expect(rapid().getByText('API key configured')).toBeInTheDocument()
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false })
  await act(async () => { del.resolve({}) }); await tick(250)
  expect(rapid().queryByText('API key configured')).not.toBeInTheDocument()
  expect(rapid().getByText(/differs from your last action/i)).toBeInTheDocument()
})

for (const failing of ['save', 'remove'] as const) {
  it(`still reconciles server truth when ${failing} fails`, async () => {
    const save = deferred<object>(); const del = deferred<object>()
    vi.mocked(api.rapidApiApi.save).mockReturnValue(save.promise as never); vi.mocked(api.rapidApiApi.remove).mockReturnValue(del.promise as never)
    open(); await tick(); await replace(); await remove()
    vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: failing === 'remove' })
    await act(async () => { if (failing === 'save') save.reject(new Error('save failed')); else save.resolve({}); if (failing === 'remove') del.reject(new Error('remove failed')); else del.resolve({}) })
    await tick(250)
    expect(Boolean(rapid().queryByText('API key configured'))).toBe(failing === 'remove')
    expect(api.rapidApiApi.status).toHaveBeenCalledTimes(2)
  })
}

it('bounds status reads and Retry clears uncertainty when the read succeeds', async () => {
  open(); await tick(); await replace()
  vi.mocked(api.rapidApiApi.status).mockReturnValueOnce(new Promise(() => {}))
  await tick(8250)
  expect(rapid().getByText(/configuration is unconfirmed/i)).toBeInTheDocument()
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false })
  await act(async () => { fireEvent.click(rapid().getByRole('button', { name: /^retry$/i })) })
  expect(rapid().queryByText(/configuration is unconfirmed/i)).not.toBeInTheDocument()
  expect(rapid().queryByText('API key configured')).not.toBeInTheDocument()
})

it('keeps a timed-out write uncertain through follow-up reads and reconciles its late commit', async () => {
  const save = deferred<object>(); vi.mocked(api.rapidApiApi.save).mockReturnValue(save.promise as never)
  open(); await tick(); await replace(); await tick(30000)
  expect(rapid().getByRole('button', { name: /^replace$/i })).not.toBeDisabled()
  expect(rapid().getByText(/configuration is unconfirmed/i)).toBeInTheDocument()
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false })
  await tick(2250); expect(rapid().queryByText('API key configured')).not.toBeInTheDocument()
  expect(rapid().getByText(/configuration is unconfirmed/i)).toBeInTheDocument()
  fields('keep-new-typing', 'other-user')
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: true })
  await act(async () => { save.resolve({}) }); await tick(250)
  expect(rapid().getByText('API key configured')).toBeInTheDocument()
  expect(rapid().queryByText(/configuration is unconfirmed/i)).not.toBeInTheDocument()
  expect(rapid().getByPlaceholderText('New RapidAPI key…')).toHaveValue('keep-new-typing')
})

it('stops automatic follow-ups after the bounded window and leaves explicit Retry', async () => {
  vi.mocked(api.rapidApiApi.save).mockReturnValue(new Promise(() => {}))
  open(); await tick(); await replace(); await tick(61000)
  const reads = vi.mocked(api.rapidApiApi.status).mock.calls.length
  expect(reads).toBeGreaterThan(1); expect(reads).toBeLessThanOrEqual(31)
  await tick(60000); expect(api.rapidApiApi.status).toHaveBeenCalledTimes(reads)
  expect(rapid().getByText(/configuration is unconfirmed/i)).toBeInTheDocument()
  expect(rapid().getByRole('button', { name: /^retry$/i })).toBeInTheDocument()
})

it('a late Retry response cannot outvote a mutation', async () => {
  open(); await tick(); await replace()
  vi.mocked(api.rapidApiApi.status).mockRejectedValueOnce(new Error('status unavailable'))
  await tick(250)
  const read = deferred<{ configured: boolean }>(); vi.mocked(api.rapidApiApi.status).mockReturnValueOnce(read.promise)
  await act(async () => { fireEvent.click(rapid().getByRole('button', { name: /^retry$/i })) })
  await remove(); await act(async () => { read.resolve({ configured: true }) })
  expect(rapid().queryByText('API key configured')).not.toBeInTheDocument()
})

it('does not confirm an old snapshot after a lost write response while the server can still commit', async () => {
  const responseLost = Object.assign(new Error('Could not reach the server'), { outcomeUncertain: true })
  open(); await tick()
  vi.mocked(api.rapidApiApi.save).mockRejectedValueOnce(responseLost)
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false })
  await replace(); await tick(250)
  expect(rapid().getByText(/configuration is unconfirmed/i)).toBeInTheDocument()
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: true })
  await tick(1000)
  expect(rapid().getByText('API key configured')).toBeInTheDocument()
  expect(rapid().getByText(/configuration is unconfirmed/i)).toBeInTheDocument()
})

it('keeps uncertainty if an already timed-out write later loses its response', async () => {
  const save = deferred<object>(); vi.mocked(api.rapidApiApi.save).mockReturnValue(save.promise as never)
  open(); await tick(); await replace(); await tick(30000)
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false })
  await act(async () => { save.reject(Object.assign(new Error('connection lost'), { outcomeUncertain: true })) })
  await tick(250)
  expect(rapid().getByText(/configuration is unconfirmed/i)).toBeInTheDocument()
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: true }); await tick(1000)
  expect(rapid().getByText('API key configured')).toBeInTheDocument()
  expect(rapid().getByText(/configuration is unconfirmed/i)).toBeInTheDocument()
})

it('clears its removal timeout error when that same write later succeeds', async () => {
  const del = deferred<object>(); vi.mocked(api.rapidApiApi.remove).mockReturnValue(del.promise as never)
  open(); await tick(); await remove(); await tick(30000)
  expect(rapid().getByText(/couldn't remove/i)).toBeInTheDocument()
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false })
  await act(async () => { del.resolve({}) }); await tick(250)
  expect(rapid().queryByText(/couldn't remove/i)).not.toBeInTheDocument()
  expect(rapid().queryByText('API key configured')).not.toBeInTheDocument()
})

it('a late successful removal cannot erase a newer mutation error', async () => {
  const del = deferred<object>(); vi.mocked(api.rapidApiApi.remove).mockReturnValue(del.promise as never)
  open(); await tick(); await remove(); await tick(30000)
  vi.mocked(api.rapidApiApi.save).mockRejectedValueOnce(new Error('newer replacement failed'))
  await replace(); await act(async () => { del.resolve({}) }); await tick(250)
  expect(rapid().getByText('newer replacement failed')).toBeInTheDocument()
})
