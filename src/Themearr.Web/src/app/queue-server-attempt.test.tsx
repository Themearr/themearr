import { act, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AuthProvider } from '@/lib/auth'
vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
const api = await import('@/lib/api')
const { default: QueuePage } = await import('./queue/page')
const item = (id: string, title: string) => ({ id, source: 'plex', sourceRef: id, title, year: 2020, sourcePath: null, folderName: title, status: 'pending', posterUrl: null })
const finished = { inProgress: false, finished: true, error: null, logs: ['OTHER ATTEMPT LOG'], attemptId: 'old', state: 'succeeded', known: true }
async function flush(ms = 50) { await act(async () => { await vi.advanceTimersByTimeAsync(ms) }) }
beforeEach(() => {
  vi.clearAllMocks(); vi.useFakeTimers()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false } as never)
  for (const surface of [api.moviesApi, api.showsApi]) {
    vi.mocked(surface.list).mockResolvedValue([item('same', 'First'), item('next', 'Next')] as never)
    vi.mocked(surface.search).mockResolvedValue({ results: [{ videoId: 'v', title: 'Theme', thumbnail: null, duration: null, channel: '', score: 100, bestMatch: true }] } as never)
    vi.mocked(surface.download).mockResolvedValue({ started: true, attemptId: 'accepted' } as never)
    vi.mocked(surface.downloadUrl).mockResolvedValue({ started: true, attemptId: 'accepted' } as never)
    vi.mocked(surface.downloadStatus).mockResolvedValue(finished as never)
  }
  vi.mocked(api.moviesApi.autoDownload).mockResolvedValue({ started: true, attemptId: 'accepted' } as never)
})
afterEach(() => vi.useRealTimers())
async function open(media: 'movies' | 'shows') {
  render(<MemoryRouter><AuthProvider><QueuePage /></AuthProvider></MemoryRouter>); await flush()
  if (media === 'shows') { fireEvent.click(screen.getByRole('button', { name: /^shows$/i })); await flush() }
}
async function start(path: 'url' | 'provider') {
  if (path === 'url') {
    fireEvent.change(screen.getByPlaceholderText('https://www.youtube.com/watch?v=…'), { target: { value: 'https://example.com/theme.mp3' } })
    await act(async () => { fireEvent.click(screen.getAllByRole('button', { name: /^download$/i }).at(-1)!) })
  } else await act(async () => { fireEvent.click(screen.getAllByRole('button', { name: /^download$/i })[0]) })
}
for (const media of ['movies', 'shows'] as const) {
  for (const path of ['url', 'provider'] as const) {
    it(`${media}/${path} never advances from another attempt's terminal snapshot or logs`, async () => {
      await open(media); await start(path); await flush(1100)
      expect(screen.getByText(`2 ${media} left in queue`)).toBeInTheDocument()
      expect(screen.queryByText('OTHER ATTEMPT LOG')).not.toBeInTheDocument()
      expect(screen.getByRole('button', { name: /^skip$/i })).not.toBeDisabled()
      expect(screen.getByText(/lost tracking/i)).toBeInTheDocument()
      const status = media === 'movies' ? api.moviesApi.downloadStatus : api.showsApi.downloadStatus
      expect(vi.mocked(status).mock.calls[0][2]).toBe('accepted')
    })
  }
  for (const state of ['unknown', 'superseded'] as const) {
    it(`${media} releases tracking on ${state} without retrying`, async () => {
      const surface = media === 'movies' ? api.moviesApi : api.showsApi
      vi.mocked(surface.downloadStatus).mockResolvedValue({ ...finished, finished: false, state, attemptId: state === 'unknown' ? null : 'accepted', logs: [] } as never)
      await open(media); await start('provider'); await flush(5100)
      expect(screen.getByRole('button', { name: /^skip$/i })).not.toBeDisabled()
      expect(screen.getByText(`2 ${media} left in queue`)).toBeInTheDocument()
      expect(surface.download).toHaveBeenCalledTimes(1)
      expect(screen.getByText(/lost tracking/i)).toBeInTheDocument()
    })
  }
}

for (const media of ['movies', 'shows'] as const) {
  it(`${media} auto mode stops on lost tracking without advancing or spending quota again`, async () => {
    const surface = media === 'movies' ? api.moviesApi : api.showsApi
    vi.mocked(surface.downloadStatus).mockResolvedValue({ ...finished, finished: false, state: 'superseded', attemptId: 'accepted', logs: [] } as never)
    if (media === 'movies') vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: true } as never)
    await open(media)
    if (media === 'shows') {
      vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false } as never)
      await act(async () => { fireEvent.click(screen.getByRole('button', { name: /^auto$/i })) })
    }
    await flush(6100)
    expect(screen.getByText(`2 ${media} left in queue`)).toBeInTheDocument()
    expect(screen.getByText(/lost tracking/i)).toBeInTheDocument()
    const starter = media === 'movies' ? api.moviesApi.autoDownload : api.showsApi.download
    expect(starter).toHaveBeenCalledTimes(1)
    expect(vi.mocked(surface.downloadStatus).mock.calls[0][2]).toBe('accepted')
  })
}
