import type {
  Movie, YoutubeResult, PlexServer, PlexLibrary,
  SetupStatus, Settings, SyncStatus, VersionInfo,
} from './types'

const BASE = (process.env.NEXT_PUBLIC_API_URL ?? '').replace(/\/$/, '')

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { 'Content-Type': 'application/json', ...init?.headers },
    ...init,
  })
  if (!res.ok) {
    const body = await res.json().catch(() => ({ detail: res.statusText }))
    throw new Error(body.detail ?? res.statusText)
  }
  return res.json()
}

// ── Setup ─────────────────────────────────────────────────────────────────────

export const setupApi = {
  status: () => request<SetupStatus>('/api/setup/status'),

  startPlexLogin: (forwardUrl = '') =>
    request<{ pinId: number; code: string; authUrl: string }>('/api/setup/plex/login', {
      method: 'POST',
      body: JSON.stringify({ forwardUrl }),
    }),

  plexLoginStatus: (pinId: number, code: string) =>
    request<{ claimed: boolean; connected: boolean; accountName?: string }>
      (`/api/setup/plex/login/status?pinId=${pinId}&code=${encodeURIComponent(code)}`),

  plexServers: () =>
    request<{ servers: PlexServer[] }>('/api/setup/plex/servers'),

  plexLibraries: (servers: PlexServer[]) =>
    request<{ libraries: Record<string, PlexLibrary[]> }>('/api/setup/plex/libraries', {
      method: 'POST',
      body: JSON.stringify({ servers }),
    }),

  logout: () =>
    request<{ success: boolean }>('/api/setup/plex/logout', { method: 'POST' }),

  saveSelection: (body: {
    servers: PlexServer[]
    selectedLibraries: Record<string, string[]>
    pathMappings: { source: string; target: string }[]
    libraryPaths: string[]
  }) =>
    request<SetupStatus>('/api/setup/plex/selection', {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  reset: () =>
    request<SetupStatus>('/api/setup/reset', { method: 'POST' }),
}

// ── Movies ────────────────────────────────────────────────────────────────────

export const moviesApi = {
  list: () => request<Movie[]>('/api/movies'),

  search: (movieId: string) =>
    request<{ movie: Movie; results: YoutubeResult[] }>(`/api/search/${encodeURIComponent(movieId)}`),

  download: (movieId: string, videoId: string) =>
    request<{ started: boolean; movieId: string }>('/api/download', {
      method: 'POST',
      body: JSON.stringify({ movieId, videoId }),
    }),

  downloadUrl: (movieId: string, url: string) =>
    request<{ started: boolean; movieId: string }>('/api/download-url', {
      method: 'POST',
      body: JSON.stringify({ movieId, url }),
    }),

  downloadStatus: (movieId: string) =>
    request<{ inProgress: boolean; finished: boolean; error: string | null }>(`/api/download/status/${encodeURIComponent(movieId)}`),
}

// ── Settings ──────────────────────────────────────────────────────────────────

export const settingsApi = {
  get: () => request<Settings>('/api/settings'),
  save: (body: Settings) =>
    request<Settings>('/api/settings', {
      method: 'POST',
      body: JSON.stringify(body),
    }),
}

// ── Sync ──────────────────────────────────────────────────────────────────────

export const syncApi = {
  start: () =>
    request<{ started: boolean; detail?: string }>('/api/sync', { method: 'POST' }),
  status: () => request<SyncStatus>('/api/sync/status'),
}

// ── Version / update ──────────────────────────────────────────────────────────

export const versionApi = {
  get:    () => request<VersionInfo>('/api/version'),
  update: () => request<{ started: boolean }>('/api/update', { method: 'POST' }),
  updateStatus: () => request<{ inProgress: boolean; finished: boolean; error: string; logs: string[] }>('/api/update/status'),
}
