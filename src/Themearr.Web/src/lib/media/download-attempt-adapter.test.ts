import { afterEach, describe, expect, it, vi } from 'vitest'
import { moviesAdapter, showsAdapter } from './adapter'

afterEach(() => vi.unstubAllGlobals())

describe('attempt-qualified media APIs', () => {
  for (const [kind, adapter] of [['movies', moviesAdapter], ['shows', showsAdapter]] as const) {
    it(`${kind} encodes attempt identity and forwards cancellation`, async () => {
      const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({ inProgress: true, finished: false, error: null, logs: [] })))
      vi.stubGlobal('fetch', fetch)
      const signal = new AbortController().signal
      await adapter.downloadStatus('item/id', { signal }, 'attempt/with space')
      expect(fetch.mock.calls[0][0]).toBe(kind === 'movies'
        ? '/api/download/status/item%2Fid?attemptId=attempt%2Fwith%20space'
        : '/api/shows/item%2Fid/download/status?attemptId=attempt%2Fwith%20space')
      expect(fetch.mock.calls[0][1].signal).toBe(signal)
    })
    it(`${kind} keeps legacy URLs when no attempt is supplied`, async () => {
      const fetch = vi.fn().mockResolvedValue(new Response('{}')); vi.stubGlobal('fetch', fetch)
      await adapter.downloadStatus('id')
      expect(fetch.mock.calls[0][0]).not.toContain('?')
    })
  }
  it('composed show auto-download returns the accepted server identity', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({ results: [{ videoId: 'v', bestMatch: true }] })))
      .mockResolvedValueOnce(new Response(JSON.stringify({ started: true, showId: 'id', attemptId: 'server-attempt' })))
    vi.stubGlobal('fetch', fetch)
    expect(await showsAdapter.autoDownload('id')).toMatchObject({ attemptId: 'server-attempt' })
  })
})
