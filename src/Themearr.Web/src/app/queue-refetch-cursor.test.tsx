import { act, fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthProvider } from '@/lib/auth'
import QueuePage from './queue/page'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
const api = await import('@/lib/api')

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(yes => { resolve = yes })
  return { promise, resolve }
}
const item = (id: string) => ({ id, title: `Movie ${id.toUpperCase()}`, source: 'plex', sourceRef: id,
  sourcePath: null, folderName: `/movies/${id}`, status: 'pending', year: 2001, posterUrl: null })
const tick = (ms = 1) => act(async () => { await vi.advanceTimersByTimeAsync(ms) })
const click = (name: RegExp) => act(async () => { fireEvent.click(screen.getByRole('button', { name })) })

beforeEach(() => {
  vi.clearAllMocks()
  vi.useFakeTimers()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: true, setupComplete: true } as never)
  vi.mocked(api.versionApi.get).mockResolvedValue({ current: 'v1', latest: 'v1', updateAvailable: false } as never)
  vi.mocked(api.systemApi.health).mockResolvedValue({ status: 'ok', checks: [] } as never)
  vi.mocked(api.settingsApi.get).mockResolvedValue({ autoDownload: false } as never)
  vi.mocked(api.syncApi.status).mockResolvedValue({ inProgress: false, finished: false } as never)
  vi.mocked(api.moviesApi.list).mockResolvedValue([item('a'), item('b'), item('c')] as never)
  vi.mocked(api.moviesApi.search).mockResolvedValue({ results: [{ videoId: 'theme', title: 'Theme',
    thumbnail: null, duration: null, channel: 'channel', score: 1, bestMatch: false }] } as never)
  vi.mocked(api.moviesApi.download).mockResolvedValue({ started: true, movieId: 'a' } as never)
  vi.mocked(api.moviesApi.downloadStatus).mockResolvedValue({ inProgress: true, finished: false, error: null, logs: [] })
})
afterEach(() => { vi.useRealTimers() })

async function openWithPendingRefetch() {
  render(<MemoryRouter><AuthProvider><QueuePage /></AuthProvider></MemoryRouter>)
  await tick()
  const next = deferred<ReturnType<typeof item>[]>()
  const other = deferred<ReturnType<typeof item>[]>()
  vi.mocked(api.showsApi.list).mockReturnValue(other.promise as never)
  vi.mocked(api.moviesApi.list).mockReturnValue(next.promise as never)
  return { next }
}
async function switchAwayAndBack() {
  await click(/^Shows$/)
  await tick()
  await click(/^Movies$/)
  await tick()
}

describe('queue selection survives a pending same-media refetch', () => {
  it('finishing A before [B,C] lands still shows B next', async () => {
    const { next } = await openWithPendingRefetch()
    await act(async () => { fireEvent.click(screen.getAllByRole('button', { name: /^Download$/ })[0]) })
    await switchAwayAndBack()
    vi.mocked(api.moviesApi.downloadStatus).mockResolvedValue({ inProgress: false, finished: true, error: null, logs: [] })
    await tick(1100)
    await act(async () => { next.resolve([item('b'), item('c')]) })
    expect(screen.getByText('2 movies left in queue')).toBeInTheDocument()
    expect(vi.mocked(api.moviesApi.search).mock.calls.at(-1)?.[0]).toBe('b')
    expect(screen.queryByText('All caught up!')).not.toBeInTheDocument()
  })

  it('Skip before a shrinking [B] refetch cannot report caught up', async () => {
    const { next } = await openWithPendingRefetch()
    await switchAwayAndBack()
    await click(/^Skip$/)
    await act(async () => { next.resolve([item('b')]) })
    expect(screen.getByText('1 movie left in queue')).toBeInTheDocument()
    expect(vi.mocked(api.moviesApi.search).mock.calls.at(-1)?.[0]).toBe('b')
    expect(screen.queryByText('All caught up!')).not.toBeInTheDocument()
  })

  it('an explicit Up next selection retains its key when a refetch reorders items', async () => {
    const { next } = await openWithPendingRefetch()
    await switchAwayAndBack()
    await click(/Movie B/)
    await act(async () => { next.resolve([item('b'), item('c'), item('a')]) })
    expect(vi.mocked(api.moviesApi.search).mock.calls.at(-1)?.[0]).toBe('b')
    expect(screen.getByText('2 movies left in queue')).toBeInTheDocument()
  })

  it('an ignored result removed by a refetch does not skip its successor', async () => {
    const { next } = await openWithPendingRefetch()
    vi.mocked(api.moviesApi.ignoreMovie).mockResolvedValue({ ignored: true })
    await switchAwayAndBack()
    await click(/^Ignore$/)
    await act(async () => { next.resolve([item('b'), item('c')]) })
    expect(vi.mocked(api.moviesApi.search).mock.calls.at(-1)?.[0]).toBe('b')
    expect(screen.getByText('2 movies left in queue')).toBeInTheDocument()
  })
  it('jumping after a reorder does not discard an item wrapped into Up next', async () => {
    vi.mocked(api.moviesApi.list).mockResolvedValue([item('a'), item('b'), item('c'), item('d')] as never)
    const { next } = await openWithPendingRefetch()
    await switchAwayAndBack()
    await click(/Movie B/)
    await act(async () => { next.resolve([item('c'), item('b'), item('d')]) })
    await click(/Movie D/)
    expect(screen.getByText('2 movies left in queue')).toBeInTheDocument()
    await click(/^Skip$/)
    expect(vi.mocked(api.moviesApi.search).mock.calls.at(-1)?.[0]).toBe('c')
    expect(screen.getByText('1 movie left in queue')).toBeInTheDocument()
  })

})
