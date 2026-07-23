import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest'

// These tests exercise the REAL request() wrapper (not the @/test/apiMock), so
// they stub the global fetch and call an ordinary api method through it. The
// point is request()'s error contract: what a caller's `catch (e) => e.message`
// actually shows the user. moviesApi.list() is the simplest GET path.
import { moviesApi } from './api'

function res(init: Partial<{ ok: boolean; status: number; statusText: string; json: () => Promise<unknown> }>) {
  return {
    ok: init.ok ?? true,
    status: init.status ?? 200,
    statusText: init.statusText ?? '',
    json: init.json ?? (() => Promise.resolve({})),
  }
}

beforeEach(() => {
  localStorage.clear()
})

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('request() error contract', () => {
  it('a network failure surfaces an honest message, not the browser raw "Failed to fetch"', async () => {
    // What fetch really throws when the server is unreachable.
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')))

    await expect(moviesApi.list()).rejects.toThrow(/could not reach the server/i)
    // And it must not leak the raw browser string a user can't act on.
    await expect(moviesApi.list()).rejects.not.toThrow(/failed to fetch/i)
  })

  it('an OK response with a non-JSON body surfaces an honest message, not a raw SyntaxError', async () => {
    // e.g. a proxy returns 200 + an HTML error page; res.json() throws SyntaxError.
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(res({
      ok: true, status: 200,
      json: () => Promise.reject(new SyntaxError('Unexpected token < in JSON at position 0')),
    })))

    await expect(moviesApi.list()).rejects.toThrow(/invalid response from the server/i)
    await expect(moviesApi.list()).rejects.not.toThrow(/unexpected token/i)
  })

  it('preserves a server-supplied error detail (regression guard for the refactor)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(res({
      ok: false, status: 500, statusText: 'Internal Server Error',
      json: () => Promise.resolve({ detail: 'Quota exhausted' }),
    })))

    await expect(moviesApi.list()).rejects.toThrow('Quota exhausted')
  })

  it('falls back to statusText when an error response has no usable body (regression guard)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(res({
      ok: false, status: 502, statusText: 'Bad Gateway',
      json: () => Promise.reject(new SyntaxError('Unexpected token < in JSON at position 0')),
    })))

    await expect(moviesApi.list()).rejects.toThrow('Bad Gateway')
  })
})
