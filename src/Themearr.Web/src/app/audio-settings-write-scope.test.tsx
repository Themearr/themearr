import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'
import { AuthProvider } from '@/lib/auth'
vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
const api = await import('@/lib/api')
const { default: SettingsPage } = await import('./settings/page')
const { default: QueuePage } = await import('./queue/page')
const { SetupWizard } = await import('@/components/setup/SetupWizard')
const options = { enabled: true, trimSeconds: 30, normalize: false, bitrateKbps: 192 }
const stored = { selectedServers: [{ id: 's', name: 'Server', url: 'http://plex' }], selectedLibraries: { s: ['1'] }, selectedShowLibraries: { s: ['2'] }, pathMappings: [], libraryPaths: ['/media'], advanced: { maxSearchDirs: 20000, searchDepth: 4 }, autoDownload: false, autoSync: false, lastAutoSyncAt: '', audioProcessing: options }
function deferred() { let resolve!: () => void; const promise = new Promise<void>(r => { resolve = r }); return { promise, resolve } }
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ setupComplete: true, plexConnected: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue(stored as never)
  vi.mocked(api.settingsApi.plexLibraries).mockResolvedValue({ libraries: { s: [{ key: '1', title: 'Films', type: 'movie' }, { key: '2', title: 'Series', type: 'show' }] } })
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false })
  vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
  vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'key' } as never)
  vi.mocked(api.settingsApi.save).mockResolvedValue({} as never)
  vi.mocked(api.moviesApi.autoDownload).mockResolvedValue({ started: true })
  vi.mocked(api.moviesApi.downloadStatus).mockResolvedValue({ inProgress: true, finished: false, error: null, logs: [] })
})
function open(ui: React.ReactNode) { render(<MemoryRouter><AuthProvider>{ui}</AuthProvider></MemoryRouter>) }
for (const media of ['movie', 'show']) {
  it(`${media} library save arriving after audio disable cannot re-enable processing`, async () => {
    const held = deferred(); let serverEnabled = true; let calls = 0
    vi.mocked(api.settingsApi.save).mockImplementation(async payload => {
      if (++calls === 1) await held.promise
      if (payload.audioProcessing !== undefined) serverEnabled = payload.audioProcessing.enabled
      return {} as never
    })
    open(<SettingsPage />); await screen.findByRole('switch', { name: /process new theme downloads/i })
    fireEvent.click(screen.getByRole('button', { name: new RegExp(`save ${media} libraries`, 'i') }))
    await waitFor(() => expect(api.settingsApi.save).toHaveBeenCalledTimes(1))
    fireEvent.click(screen.getByRole('switch', { name: /process new theme downloads/i }))
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: /^save changes$/i })) })
    expect(serverEnabled).toBe(false)
    await act(async () => { held.resolve() })
    expect(serverEnabled).toBe(false)
    expect(vi.mocked(api.settingsApi.save).mock.calls[0][0]).not.toHaveProperty('audioProcessing')
  })
}
it('queue auto-mode writes cannot replay a captured audio configuration', async () => {
  vi.mocked(api.moviesApi.list).mockResolvedValue([{ id: 'm', title: 'Item', year: 2020, status: 'pending', folderName: '/media' }] as never)
  vi.mocked(api.moviesApi.search).mockResolvedValue({ results: [] } as never)
  open(<QueuePage />); const toggle = await screen.findByRole('button', { name: /^auto$/i })
  await act(async () => { fireEvent.click(toggle) })
  expect(vi.mocked(api.settingsApi.save).mock.calls[0][0]).not.toHaveProperty('audioProcessing')
})
it('Radarr setup writes paths without replaying the audio configuration', async () => {
  vi.mocked(api.radarrApi.test).mockResolvedValue({ ok: true, detail: 'Connected' } as never)
  vi.mocked(api.radarrApi.save).mockResolvedValue({} as never)
  open(<SetupWizard />)
  fireEvent.click(await screen.findByText('Radarr'))
  fireEvent.change(screen.getByPlaceholderText('http://localhost:7878'), { target: { value: 'http://radarr:7878' } })
  fireEvent.change(screen.getByPlaceholderText('Radarr API key…'), { target: { value: 'secret' } })
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: /test connection/i })) })
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: /^continue$/i })) })
  fireEvent.change(screen.getByPlaceholderText('/mnt/movies'), { target: { value: '/media' } })
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: /save & continue/i })) })
  expect(vi.mocked(api.settingsApi.save).mock.calls[0][0]).not.toHaveProperty('audioProcessing')
})
