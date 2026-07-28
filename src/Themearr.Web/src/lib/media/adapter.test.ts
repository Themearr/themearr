import { describe, it, expect, vi } from 'vitest'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')
const { moviesAdapter, showsAdapter } = await import('@/lib/media/adapter')

describe('media adapters', () => {
  it('movies expose three statuses and shows expose four', () => {
    expect(moviesAdapter.statuses).toEqual(['pending', 'downloaded', 'ignored'])
    expect(showsAdapter.statuses).toEqual(['pending', 'downloaded', 'plexTheme', 'ignored'])
  })

  it('each adapter routes to its own API surface', async () => {
    await moviesAdapter.ignore('m1')
    expect(api.moviesApi.ignoreMovie).toHaveBeenCalledWith('m1')
    expect(api.showsApi.ignoreShow).not.toHaveBeenCalled()

    await showsAdapter.ignore('s1')
    expect(api.showsApi.ignoreShow).toHaveBeenCalledWith('s1')
  })

  it('search normalises both shapes to { results }', async () => {
    vi.mocked(api.moviesApi.search).mockResolvedValue({ movie: {}, results: [{ videoId: 'v1' }] } as never)
    vi.mocked(api.showsApi.search).mockResolvedValue({ show: {}, results: [{ videoId: 'v2' }] } as never)

    expect((await moviesAdapter.search('m1')).results[0].videoId).toBe('v1')
    expect((await showsAdapter.search('s1')).results[0].videoId).toBe('v2')
  })

  it('labels differ so the grid copy is media-appropriate', () => {
    expect(moviesAdapter.labels.searchPlaceholder).toBe('Search movies…')
    expect(showsAdapter.labels.searchPlaceholder).toBe('Search shows…')
  })
})
