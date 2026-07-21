import { renderHook, waitFor, act } from '@testing-library/react'
import { describe, it, expect, vi } from 'vitest'
import { useResource } from '@/lib/useResource'

describe('useResource', () => {
  it('starts loading, then exposes the data', async () => {
    const { result } = renderHook(() => useResource(() => Promise.resolve(['a', 'b'])))

    expect(result.current.loading).toBe(true)
    await waitFor(() => expect(result.current.loading).toBe(false))

    expect(result.current.data).toEqual(['a', 'b'])
    expect(result.current.error).toBeNull()
  })

  it('exposes an error and leaves data null when the fetch fails', async () => {
    const { result } = renderHook(() =>
      useResource(() => Promise.reject(new Error('boom'))))

    await waitFor(() => expect(result.current.loading).toBe(false))

    expect(result.current.error).not.toBeNull()
    // The whole point: a failure must not look like an empty result.
    expect(result.current.data).toBeNull()
  })

  it('retry clears the error and fetches again', async () => {
    let attempt = 0
    const fetcher = vi.fn(() => {
      attempt++
      return attempt === 1 ? Promise.reject(new Error('boom')) : Promise.resolve(['ok'])
    })
    const { result } = renderHook(() => useResource(fetcher))
    await waitFor(() => expect(result.current.error).not.toBeNull())

    act(() => result.current.retry())

    await waitFor(() => expect(result.current.data).toEqual(['ok']))
    expect(result.current.error).toBeNull()
    expect(fetcher).toHaveBeenCalledTimes(2)
  })

  it('ignores a slow first response that settles after a retry', async () => {
    // Without this guard a stale response can overwrite a newer one.
    let resolveFirst: (v: string[]) => void = () => {}
    let call = 0
    const fetcher = () => {
      call++
      return call === 1
        ? new Promise<string[]>(res => { resolveFirst = res })
        : Promise.resolve(['second'])
    }
    const { result } = renderHook(() => useResource(fetcher))

    act(() => result.current.retry())
    await waitFor(() => expect(result.current.data).toEqual(['second']))
    // `await act(async ...)` (not the synchronous `act(() => ...)`) so the stale
    // promise's `.then` microtask is flushed before the assertion below runs.
    // Without this, `waitFor`'s first synchronous check below observes data
    // still equal to ['second'] and resolves before the stale update lands,
    // silently passing regardless of whether the hook's guard exists.
    await act(async () => { resolveFirst(['first']) })

    await waitFor(() => expect(result.current.data).toEqual(['second']))
  })
})
