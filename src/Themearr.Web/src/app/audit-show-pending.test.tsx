import { act, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, expect, it, vi } from 'vitest'
import { AuthProvider } from '@/lib/auth'
vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
const api = await import('@/lib/api')
const Shows = (await import('./shows/page')).default
afterEach(() => vi.useRealTimers())
it('waits for a queued show sync instead of accepting a previous idle result', async () => {
  vi.useFakeTimers()
  vi.mocked(api.setupApi.status).mockResolvedValue({ setupComplete: true, plexConnected: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({ selectedShowLibraries: { server: ['1'] } } as never)
  vi.mocked(api.showsApi.list).mockResolvedValue([])
  vi.mocked(api.systemApi.runTask).mockResolvedValue({ started: true })
  vi.mocked(api.systemApi.tasks).mockResolvedValue([{ id: 'syncShows', isRunning: false, isPending: true, lastResult: 'old result' }] as never)
  await act(async () => { render(<MemoryRouter><AuthProvider><Shows /></AuthProvider></MemoryRouter>) })
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: /sync shows/i })) })
  await act(async () => { await vi.advanceTimersByTimeAsync(2100) })
  expect(screen.getByText(/Syncing shows from Plex/i)).toBeInTheDocument()
  expect(screen.queryByText('old result')).not.toBeInTheDocument()
  vi.mocked(api.systemApi.tasks).mockResolvedValue([{ id: 'syncShows', isRunning: false, isPending: false, lastResult: 'synced 253 shows' }] as never)
  await act(async () => { await vi.advanceTimersByTimeAsync(2100) })
  expect(screen.getByText('synced 253 shows')).toBeInTheDocument()
})
