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
  ignore(id: string): Promise<unknown>
  unignore(id: string): Promise<unknown>
  deleteTheme(id: string): Promise<{ deleted: boolean }>
  themeAudioObjectUrl(id: string): Promise<string>

  /** Which filter chips the grid renders, in order. */
  statuses: MediaStatus[]
  labels: { plural: string; searchPlaceholder: string; emptyTitle: string }
}

export const moviesAdapter: MediaAdapter = {
  list:                () => moviesApi.list(),
  search:              (id, q) => moviesApi.search(id, q),
  download:            (id, videoId) => moviesApi.download(id, videoId),
  downloadUrl:         (id, url) => moviesApi.downloadUrl(id, url),
  downloadStatus:      (id, init) => moviesApi.downloadStatus(id, init),
  ignore:              id => moviesApi.ignoreMovie(id),
  unignore:            id => moviesApi.unignoreMovie(id),
  deleteTheme:         id => moviesApi.deleteTheme(id),
  themeAudioObjectUrl: id => moviesApi.themeAudioObjectUrl(id),

  statuses: ['pending', 'downloaded', 'ignored'],
  labels: { plural: 'movies', searchPlaceholder: 'Search movies…', emptyTitle: 'No movies yet' },
}

export const showsAdapter: MediaAdapter = {
  list:                () => showsApi.list(),
  search:              (id, q) => showsApi.search(id, q),
  download:            (id, videoId) => showsApi.download(id, videoId),
  downloadUrl:         (id, url) => showsApi.downloadUrl(id, url),
  downloadStatus:      (id, init) => showsApi.downloadStatus(id, init),
  ignore:              id => showsApi.ignoreShow(id),
  unignore:            id => showsApi.unignoreShow(id),
  deleteTheme:         id => showsApi.deleteTheme(id),
  themeAudioObjectUrl: id => showsApi.themeAudioObjectUrl(id),

  // 'plexTheme' sits between downloaded and ignored: it is covered, but not by us.
  statuses: ['pending', 'downloaded', 'plexTheme', 'ignored'],
  labels: { plural: 'shows', searchPlaceholder: 'Search shows…', emptyTitle: 'No shows yet' },
}
