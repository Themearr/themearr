import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SearchModal } from './SearchModal'
import { moviesAdapter } from '@/lib/media/adapter'
const item = { id: 'a', source: 'plex', sourceRef: 'a', title: 'Arrival', year: 2016, sourcePath: null, folderName: '/movies/Arrival', status: 'downloaded' as const, posterUrl: null }
const finished = { inProgress: false, finished: true, error: null, logs: [] }
function pending<T>() { let resolve!: (v: T) => void; let reject!: (e: Error) => void; const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej }); return { promise, resolve, reject } }
function start() { fireEvent.change(screen.getByPlaceholderText(/https:\/\/www.youtube.com/), { target: { value: 'https://example.com/theme.mp3' } }); fireEvent.click(screen.getByRole('button', { name: 'Download' })) }
afterEach(() => vi.useRealTimers())
describe('search modal download ownership', () => {
  it('waits for start before accepting the previous job completion', async () => {
    vi.useFakeTimers(); const job = pending<{ started: boolean }>(); const status = vi.fn().mockResolvedValue(finished); const done = vi.fn()
    render(<SearchModal item={item} adapter={{ ...moviesAdapter, downloadUrl: () => job.promise, downloadStatus: status }} onClose={vi.fn()} onDownloaded={done} />)
    start(); await act(async () => { await vi.advanceTimersByTimeAsync(1100) })
    expect(done).not.toHaveBeenCalled(); expect(status).not.toHaveBeenCalled()
    await act(async () => { job.reject(new Error('Could not start this download')) })
    expect(screen.getByText('Could not start this download')).toBeInTheDocument()
  })
  it('ignores a status response after unmount', async () => {
    vi.useFakeTimers(); const status = pending<typeof finished>(); const done = vi.fn()
    const { unmount } = render(<SearchModal item={item} adapter={{ ...moviesAdapter, downloadUrl: async () => ({ started: true }), downloadStatus: () => status.promise }} onClose={vi.fn()} onDownloaded={done} />)
    await act(async () => { start() }); await act(async () => { await vi.advanceTimersByTimeAsync(1100) }); unmount()
    await act(async () => { status.resolve(finished) }); expect(done).not.toHaveBeenCalled()
  })
  it('returns control after repeated failed status requests', async () => {
    vi.useFakeTimers()
    render(<SearchModal item={item} adapter={{ ...moviesAdapter, downloadUrl: async () => ({ started: true }), downloadStatus: async () => { throw new Error('offline') } }} onClose={vi.fn()} onDownloaded={vi.fn()} />)
    await act(async () => { start() }); await act(async () => { await vi.advanceTimersByTimeAsync(3500) })
    expect(screen.getByRole('button', { name: 'Download' })).not.toBeDisabled(); expect(screen.getByText(/lost contact|could not track/i)).toBeInTheDocument()
  })
})

it('releases the modal when a start request never settles', async () => {
  vi.useFakeTimers()
  const job = pending<unknown>()
  render(<SearchModal item={item} adapter={{ ...moviesAdapter, downloadUrl: () => job.promise }} onClose={vi.fn()} onDownloaded={vi.fn()} />)
  await act(async () => { start() })
  await act(async () => { await vi.advanceTimersByTimeAsync(30100) })
  expect(screen.getByRole('button', { name: 'Download' })).not.toBeDisabled()
  expect(screen.getByText(/request timed out/i)).toBeInTheDocument()
})

it('never overlaps status requests when the transport ignores cancellation', async () => {
  vi.useFakeTimers()
  const job = pending<typeof finished>()
  const status = vi.fn(() => job.promise)
  render(<SearchModal item={item} adapter={{ ...moviesAdapter, downloadUrl: async () => ({ started: true }), downloadStatus: status }} onClose={vi.fn()} onDownloaded={vi.fn()} />)
  await act(async () => { start() })
  await act(async () => { await vi.advanceTimersByTimeAsync(7500) })
  expect(status).toHaveBeenCalledTimes(1)
  await act(async () => { await vi.advanceTimersByTimeAsync(23000) })
  expect(screen.getByRole('button', { name: 'Download' })).not.toBeDisabled()
  expect(screen.getByText(/lost contact/i)).toBeInTheDocument()
})
