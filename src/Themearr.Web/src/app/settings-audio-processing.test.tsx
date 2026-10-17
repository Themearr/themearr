import { act, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'
import { AuthProvider } from '@/lib/auth'
vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
const api = await import('@/lib/api')
const { default: SettingsPage } = await import('./settings/page')
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.setupApi.status).mockResolvedValue({ setupComplete: true, plexConnected: true } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({ selectedServers: [], selectedLibraries: {}, selectedShowLibraries: {}, pathMappings: [], libraryPaths: [], advanced: { maxSearchDirs: 20000, searchDepth: 4 }, autoDownload: false, autoSync: false, lastAutoSyncAt: '' } as never)
  vi.mocked(api.rapidApiApi.status).mockResolvedValue({ configured: false })
  vi.mocked(api.radarrApi.get).mockResolvedValue({ source: 'plex', url: '', configured: false } as never)
  vi.mocked(api.apiKeyApi.get).mockResolvedValue({ key: 'k'.repeat(64) } as never)
  vi.mocked(api.settingsApi.save).mockResolvedValue({} as never)
})
function open() { render(<MemoryRouter><AuthProvider><SettingsPage /></AuthProvider></MemoryRouter>) }
it('old settings responses show processing disabled and no duration trim', async () => {
  open(); const control = await screen.findByRole('switch', { name: /process new theme downloads/i })
  expect(control).toHaveAttribute('aria-checked', 'false')
  expect(screen.getByLabelText(/trim duration/i)).toHaveValue(null)
  expect(screen.getByLabelText(/normalize loudness/i)).not.toBeChecked()
})
it('the Plex preset chooses thirty seconds while opt-in remains explicit', async () => {
  open(); const preset = await screen.findByRole('button', { name: /plex.*30.second/i })
  fireEvent.click(preset)
  expect(screen.getByLabelText(/trim duration/i)).toHaveValue(30)
  expect(screen.getByRole('switch', { name: /process new theme downloads/i })).toHaveAttribute('aria-checked', 'false')
  expect(screen.getByText(/full.length.*Jellyfin|Jellyfin.*full.length/i)).toBeInTheDocument()
})
it('saves the chosen options only after explicit enable and Save', async () => {
  open(); const control = await screen.findByRole('switch', { name: /process new theme downloads/i })
  fireEvent.click(control); fireEvent.click(screen.getByRole('button', { name: /plex.*30.second/i })); fireEvent.click(screen.getByLabelText(/normalize loudness/i))
  expect(api.settingsApi.save).not.toHaveBeenCalled()
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: /^save changes$/i })) })
  expect(api.settingsApi.save).toHaveBeenCalledWith(expect.objectContaining({ audioProcessing: { enabled: true, trimSeconds: 30, normalize: true, bitrateKbps: 192 } }))
})
