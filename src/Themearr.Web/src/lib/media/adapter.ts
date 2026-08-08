import { moviesApi, showsApi } from '@/lib/api'
import type { MediaItem, MediaStatus, YoutubeResult } from '@/lib/types'

/**
 * What MediaGrid and SearchModal need from a media type. Injecting this — rather than
 * importing moviesApi directly — is what lets shows reuse the windowing, in-flight guards
 * and refresh-staleness logic instead of owning a second copy of it. Those behaviours were
 * subtle enough to need their own fix-spec once already; two copies would drift.
 */
export interface MediaAdapter {
  list(): Promise<MediaItem[]>
  search(id: string, q?: string): Promise<{ results: YoutubeResult[] }>
  download(id: string, videoId: string): Promise<unknown>
  downloadUrl(id: string, url: string): Promise<unknown>
  downloadStatus(id: string, init?: RequestInit):
    Promise<{ inProgress: boolean; finished: boolean; error: string | null; logs: string[] }>
  /**
   * Search, pick the best match and start it. Movies have a server endpoint for this;
   * shows compose it from search + download client-side, which reaches the same place —
   * the show download endpoint runs the same provider/quota pre-flight the movie
   * auto-download endpoint does.
   */
  autoDownload(id: string): Promise<unknown>
  ignore(id: string): Promise<unknown>
  unignore(id: string): Promise<unknown>
  deleteTheme(id: string): Promise<{ deleted: boolean }>
  themeAudioObjectUrl(id: string): Promise<string>

  /** Which filter chips the grid renders, in order. */
  statuses: MediaStatus[]
  /**
   * User-facing nouns. `singular` exists because the shared queue and grid write
   * sentences about one item ("3 shows left in queue", "this show is ignored"), and
   * deriving it from `plural` with a ternary is how the queue ended up telling someone
   * triaging shows that they had "91 movies left".
   */
  labels: { singular: string; plural: string; searchPlaceholder: string; emptyTitle: string }
}

export const moviesAdapter: MediaAdapter = {
  list:                () => moviesApi.list(),
  search:              (id, q) => moviesApi.search(id, q),
  download:            (id, videoId) => moviesApi.download(id, videoId),
  downloadUrl:         (id, url) => moviesApi.downloadUrl(id, url),
  downloadStatus:      (id, init) => moviesApi.downloadStatus(id, init),
  autoDownload:        id => moviesApi.autoDownload(id),
  ignore:              id => moviesApi.ignoreMovie(id),
  unignore:            id => moviesApi.unignoreMovie(id),
  deleteTheme:         id => moviesApi.deleteTheme(id),
  themeAudioObjectUrl: id => moviesApi.themeAudioObjectUrl(id),

  statuses: ['pending', 'downloaded', 'ignored'],
  labels: { singular: 'movie', plural: 'movies', searchPlaceholder: 'Search movies…', emptyTitle: 'No movies yet' },
}

export const showsAdapter: MediaAdapter = {
  list:                () => showsApi.list(),
  search:              (id, q) => showsApi.search(id, q),
  download:            (id, videoId) => showsApi.download(id, videoId),
  downloadUrl:         (id, url) => showsApi.downloadUrl(id, url),
  downloadStatus:      (id, init) => showsApi.downloadStatus(id, init),

  // Composed client-side: there is no show auto-download endpoint, and the pick is the
  // same rule the movie endpoint applies server-side (first bestMatch, else give up and
  // let the operator choose). The error text matches the movie endpoint's 422 body so
  // the queue's copy reads identically either way.
  autoDownload: async id => {
    const { results } = await showsApi.search(id)
    const best = results.find(r => r.bestMatch)
    if (!best) throw new Error('No suitable match found — please select manually.')
    return showsApi.download(id, best.videoId)
  },

  ignore:              id => showsApi.ignoreShow(id),
  unignore:            id => showsApi.unignoreShow(id),
  deleteTheme:         id => showsApi.deleteTheme(id),
  themeAudioObjectUrl: id => showsApi.themeAudioObjectUrl(id),

  // 'plexTheme' sits between downloaded and ignored: it is covered, but not by us.
  statuses: ['pending', 'downloaded', 'plexTheme', 'ignored'],
  labels: { singular: 'show', plural: 'shows', searchPlaceholder: 'Search shows…', emptyTitle: 'No shows yet' },
}
