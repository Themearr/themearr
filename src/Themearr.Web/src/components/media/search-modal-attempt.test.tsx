import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { SearchModal } from './SearchModal'
import { moviesAdapter, showsAdapter } from '@/lib/media/adapter'
import type { DownloadStatus } from '@/lib/types'

const item = { id: 'same', source: 'plex', sourceRef: 'same', title: 'Arrival', year: 2016, sourcePath: null, folderName: '/Arrival', status: 'pending' as const, posterUrl: null }
const terminal = { inProgress: false, finished: true, error: null, logs: ['OTHER ATTEMPT LOG'], attemptId: 'old', state: 'succeeded' as const, known: true }
afterEach(() => vi.useRealTimers())
async function tick(ms = 1100) { await act(async () => { await vi.advanceTimersByTimeAsync(ms) }) }

for (const [media, adapter] of [['movies', moviesAdapter], ['shows', showsAdapter]] as const) {
  for (const path of ['url', 'provider'] as const) {
    it(`${media}/${path} rejects another attempt and binds its poll to the accepted identity`, async () => {
      vi.useFakeTimers(); const status = vi.fn().mockResolvedValue(terminal); const done = vi.fn(); const close = vi.fn()
      const start = vi.fn().mockResolvedValue({ started: true, attemptId: 'accepted' })
      render(<SearchModal item={item} adapter={{ ...adapter, download: start, downloadUrl: start, downloadStatus: status,
        search: async () => ({ results: [{ videoId: 'v', title: 'Theme', channel: '', thumbnail: null, duration: null, score: 100, bestMatch: true }] }) }} onClose={close} onDownloaded={done} />)
      if (path === 'provider') {
        await act(async () => { fireEvent.click(screen.getByRole('button', { name: /search youtube/i })) })
        await act(async () => { fireEvent.click(screen.getAllByRole('button', { name: /^download$/i })[0]) })
      } else {
        fireEvent.change(screen.getByPlaceholderText('https://www.youtube.com/watch?v=…'), { target: { value: 'https://example.com/theme.mp3' } })
        await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Download' })) })
      }
      await tick()
      expect(done).not.toHaveBeenCalled(); expect(close).not.toHaveBeenCalled()
      expect(screen.queryByText('OTHER ATTEMPT LOG')).not.toBeInTheDocument()
      expect(status.mock.calls[0][2]).toBe('accepted')
      expect(screen.getByText(/lost tracking/i)).toBeInTheDocument()
    })
  }
}

for (const state of ['unknown', 'superseded'] as const) {
  it(`releases controls for ${state} without completing the item`, async () => {
    vi.useFakeTimers(); const done = vi.fn()
    const status: DownloadStatus = { ...terminal, attemptId: state === 'unknown' ? null : 'accepted', state, finished: false, logs: [] }
    render(<SearchModal item={item} adapter={{ ...moviesAdapter, downloadUrl: async () => ({ started: true, attemptId: 'accepted' }), downloadStatus: async () => status }} onClose={vi.fn()} onDownloaded={done} />)
    fireEvent.change(screen.getByPlaceholderText('https://www.youtube.com/watch?v=…'), { target: { value: 'https://example.com/theme.mp3' } })
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Download' })) }); await tick()
    expect(done).not.toHaveBeenCalled(); expect(screen.getByRole('button', { name: 'Download' })).not.toBeDisabled()
    expect(screen.getByText(/lost tracking/i)).toBeInTheDocument()
  })
}

it('accepts the matching identity', async () => {
  vi.useFakeTimers(); const done = vi.fn()
  render(<SearchModal item={item} adapter={{ ...moviesAdapter, downloadUrl: async () => ({ started: true, attemptId: 'accepted' }), downloadStatus: async () => ({ ...terminal, attemptId: 'accepted' }) }} onClose={vi.fn()} onDownloaded={done} />)
  fireEvent.change(screen.getByPlaceholderText('https://www.youtube.com/watch?v=…'), { target: { value: 'https://example.com/theme.mp3' } })
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Download' })) }); await tick()
  expect(done).toHaveBeenCalledWith('same')
})

it('accepts an old server response that omits protocol fields', async () => {
  vi.useFakeTimers(); const done = vi.fn()
  render(<SearchModal item={item} adapter={{ ...moviesAdapter, downloadUrl: async () => ({ started: true, attemptId: 'accepted' }), downloadStatus: async () => ({ inProgress: false, finished: true, error: null, logs: [] }) }} onClose={vi.fn()} onDownloaded={done} />)
  fireEvent.change(screen.getByPlaceholderText('https://www.youtube.com/watch?v=…'), { target: { value: 'https://example.com/theme.mp3' } })
  await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Download' })) }); await tick()
  expect(done).toHaveBeenCalledWith('same')
})
