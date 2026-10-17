import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, expect, it, vi } from 'vitest'
import { AuthProvider } from '@/lib/auth'
vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
const api = await import('@/lib/api')
const System = (await import('./system/page')).default
beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ setupComplete: true, plexConnected: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] })
})
async function task(isPending?: boolean) {
  vi.mocked(api.systemApi.tasks).mockResolvedValue([{ id: 'syncShows', name: 'Sync Shows', interval: '1.00:00:00', isRunning: false, isPending, lastRunUtc: null, nextRunUtc: null }] as never)
  render(<MemoryRouter><AuthProvider><System /></AuthProvider></MemoryRouter>)
  fireEvent.click(await screen.findByRole('tab', { name: /tasks/i }))
}
it('shows an accepted task as queued and prevents another Run now', async () => {
  await task(true)
  expect(await screen.findByRole('button', { name: 'Queued' })).toBeDisabled()
})
it('keeps Run now available for an older server without pending state', async () => {
  await task()
  expect(await screen.findByRole('button', { name: 'Run now' })).not.toBeDisabled()
})
