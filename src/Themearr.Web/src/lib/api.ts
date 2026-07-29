import type {
  Movie, YoutubeResult, PlexServer, PlexLibrary,
  SetupStatus, Settings, SyncStatus, VersionInfo, HistoryEntry, DashboardStats,
  HealthResponse, SystemTask, RadarrSettings, ApiKey, Show, ShowStats,
} from './types'

const BASE = (import.meta.env.VITE_API_URL ?? '').replace(/\/$/, '')

const TOKEN_KEY = 'themearr_token'

export function getAuthToken(): string {
  if (typeof window === 'undefined') return ''
  return localStorage.getItem(TOKEN_KEY) ?? ''
}

export function setAuthToken(token: string) {
  if (typeof window === 'undefined') return
  localStorage.setItem(TOKEN_KEY, token)
}

export function clearAuthToken() {
  if (typeof window === 'undefined') return
  localStorage.removeItem(TOKEN_KEY)
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const token = getAuthToken()
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...(init?.headers as Record<string, string> | undefined),
  }
  // Carve-out: /api/auth/* endpoints don't require (and shouldn't send) the bearer token.
  if (token && !path.startsWith('/api/auth/')) {
    headers['Authorization'] = `Bearer ${token}`
  }

  let res: Response
  try {
    res = await fetch(`${BASE}${path}`, { ...init, headers })
  } catch (e) {
    // fetch rejects only on a transport-level failure (server unreachable, DNS,
    // offline, CORS). The raw message -- "Failed to fetch", or Safari's "Load
    // failed" -- is meaningless to a user, so translate it to something honest
    // and actionable. Call sites interpolate this after their own static prefix.
    throw new Error('Could not reach the server', { cause: e })
  }

  if (res.status === 401 && !path.startsWith('/api/auth/')) {
    clearAuthToken()
    if (typeof window !== 'undefined' && !window.location.pathname.startsWith('/login')) {
      window.location.href = '/login'
    }
    throw new Error('Unauthorized')
  }

  if (!res.ok) {
    const body = await res.json().catch(() => ({ detail: res.statusText }))
    throw new Error(body.detail ?? res.statusText)
  }

  try {
    return await res.json()
  } catch (e) {
    // A 2xx whose body isn't JSON -- e.g. a reverse proxy that answers 200 with
    // an HTML error/login page -- would otherwise surface a raw SyntaxError.
    throw new Error('Invalid response from the server', { cause: e })
  }
}

// ── Auth ──────────────────────────────────────────────────────────────────────

export const authApi = {
  verify: (token: string) =>
    request<{ ok: boolean }>('/api/auth/verify', {
      method: 'POST',
      body: JSON.stringify({ token }),
    }),
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

  // For a non-Plex install: the Plex branch reaches setup_complete through
  // saveSelection above; a Radarr user never touches that endpoint.
  complete: () => request<{ setupComplete: boolean }>('/api/setup/complete', { method: 'POST' }),
}

// ── Movies ────────────────────────────────────────────────────────────────────

export const moviesApi = {
  list: () => request<Movie[]>('/api/movies'),

  search: (movieId: string, q?: string) =>
    request<{ movie: Movie; results: YoutubeResult[] }>(
      `/api/search/${encodeURIComponent(movieId)}${q ? `?q=${encodeURIComponent(q)}` : ''}`
    ),

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

  // `init` lets a caller pass an AbortSignal (e.g. the queue's polled status
  // check bounds it with a timeout so a hung request can't wedge the poll's
  // in-flight guard forever). Every other call site omits it and behaves as before.
  downloadStatus: (movieId: string, init?: RequestInit) =>
    request<{ inProgress: boolean; finished: boolean; error: string | null; logs: string[] }>(`/api/download/status/${encodeURIComponent(movieId)}`, init),

  autoDownload: (movieId: string) =>
    request<{ started: boolean; movieId: string; videoId: string; videoTitle: string }>(`/api/auto-download/${encodeURIComponent(movieId)}`, { method: 'POST' }),

  deleteTheme: (movieId: string) =>
    request<{ deleted: boolean }>(`/api/movies/${encodeURIComponent(movieId)}/theme`, { method: 'DELETE' }),

  ignoreMovie: (movieId: string) =>
    request<{ ignored: boolean }>(`/api/movies/${encodeURIComponent(movieId)}/ignore`, { method: 'POST' }),

  unignoreMovie: (movieId: string) =>
    request<{ ignored: boolean }>(`/api/movies/${encodeURIComponent(movieId)}/unignore`, { method: 'POST' }),

  // Fetch the theme audio as a blob using the bearer token and return an object URL.
  // Caller is responsible for revoking the URL when it's no longer needed.
  themeAudioObjectUrl: async (movieId: string) => {
    const token = getAuthToken()
    const res = await fetch(
      `${BASE}/api/movies/${encodeURIComponent(movieId)}/theme/audio`,
      { headers: token ? { Authorization: `Bearer ${token}` } : undefined },
    )
    if (res.status === 401) {
      clearAuthToken()
      if (typeof window !== 'undefined') window.location.href = '/login'
      throw new Error('Unauthorized')
    }
    if (!res.ok) throw new Error(`Audio fetch failed (${res.status})`)
    const blob = await res.blob()
    return URL.createObjectURL(blob)
  },
}

// ── Settings ──────────────────────────────────────────────────────────────────

// ── Shows ─────────────────────────────────────────────────────────────────────
// Namespaced under /api/shows, unlike the movie routes' legacy un-namespaced paths.

export const showsApi = {
  list: () => request<Show[]>('/api/shows'),

  search: (showId: string, q?: string) =>
    request<{ show: Show; results: YoutubeResult[] }>(
      `/api/shows/${encodeURIComponent(showId)}/search${q ? `?q=${encodeURIComponent(q)}` : ''}`
    ),

  download: (showId: string, videoId: string) =>
    request<{ started: boolean; showId: string }>(`/api/shows/${encodeURIComponent(showId)}/download`, {
      method: 'POST',
      body: JSON.stringify({ videoId }),
    }),

  downloadUrl: (showId: string, url: string) =>
    request<{ started: boolean; showId: string }>(`/api/shows/${encodeURIComponent(showId)}/download-url`, {
      method: 'POST',
      body: JSON.stringify({ url }),
    }),

  downloadStatus: (showId: string, init?: RequestInit) =>
    request<{ inProgress: boolean; finished: boolean; error: string | null; logs: string[] }>(
      `/api/shows/${encodeURIComponent(showId)}/download/status`, init),

  deleteTheme: (showId: string) =>
    request<{ deleted: boolean }>(`/api/shows/${encodeURIComponent(showId)}/theme`, { method: 'DELETE' }),

  ignoreShow: (showId: string) =>
    request<{ ignored: boolean }>(`/api/shows/${encodeURIComponent(showId)}/ignore`, { method: 'POST' }),

  unignoreShow: (showId: string) =>
    request<{ ignored: boolean }>(`/api/shows/${encodeURIComponent(showId)}/unignore`, { method: 'POST' }),

  stats: () => request<ShowStats>('/api/stats/shows'),

  // Same bearer-fetch-to-object-URL dance as moviesApi.themeAudioObjectUrl: an <audio>
  // element can't send an Authorization header. Caller revokes the URL.
  themeAudioObjectUrl: async (showId: string) => {
    const token = getAuthToken()
    const res = await fetch(
      `${BASE}/api/shows/${encodeURIComponent(showId)}/theme/audio`,
      { headers: token ? { Authorization: `Bearer ${token}` } : undefined },
    )
    if (res.status === 401) {
      clearAuthToken()
      if (typeof window !== 'undefined') window.location.href = '/login'
      throw new Error('Unauthorized')
    }
    if (!res.ok) throw new Error(`Audio fetch failed (${res.status})`)
    const blob = await res.blob()
    return URL.createObjectURL(blob)
  },
}

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

// ── History ───────────────────────────────────────────────────────────────────

export const historyApi = {
  get: () => request<HistoryEntry[]>('/api/history'),
}


// ── RapidAPI key ──────────────────────────────────────────────────────────────

export const rapidApiApi = {
  status: () => request<{ configured: boolean }>('/api/settings/rapidapi'),

  save: (key: string, username: string) =>
    request<{ configured: boolean }>('/api/settings/rapidapi', {
      method: 'POST',
      body: JSON.stringify({ key, username }),
    }),

  remove: () => request<{ configured: boolean }>('/api/settings/rapidapi', { method: 'DELETE' }),
}

// ── Stats ─────────────────────────────────────────────────────────────────────

export const statsApi = {
  get: () => request<DashboardStats>('/api/stats'),
}

// ── Version / update ──────────────────────────────────────────────────────────

export const versionApi = {
  get:     () => request<VersionInfo>('/api/version'),
  refresh: () => request<VersionInfo>('/api/version/refresh', { method: 'POST' }),
  update:  () => request<{ started: boolean }>('/api/update', { method: 'POST' }),
  updateStatus: () => request<{ inProgress: boolean; finished: boolean; error: string; logs: string[] }>('/api/update/status'),
}

// ── System (health + tasks) ───────────────────────────────────────────────────

export const systemApi = {
  health: () => request<HealthResponse>('/api/system/health'),
  tasks:  () => request<SystemTask[]>('/api/system/tasks'),
  runTask: (id: string) =>
    request<{ started: boolean }>(`/api/system/tasks/${encodeURIComponent(id)}/run`, {
      method: 'POST',
    }),
}

// ── Library source (Radarr) ───────────────────────────────────────────────────

export const radarrApi = {
  get: () => request<RadarrSettings>('/api/settings/radarr'),
  save: (source: string, url: string, apiKey: string) =>
    request<{ source: string; configured: boolean }>('/api/settings/radarr', {
      method: 'POST',
      body: JSON.stringify({ source, url, apiKey }),
    }),
  test: (url: string, apiKey: string) =>
    request<{ ok: boolean; detail: string }>('/api/settings/radarr/test', {
      method: 'POST',
      body: JSON.stringify({ source: 'radarr', url, apiKey }),
    }),
}

// ── Plex server (manual URL override) ─────────────────────────────────────────

export const plexApi = {
  test: (serverId: string, url: string) =>
    request<{ ok: boolean; detail: string }>('/api/settings/plex/test', {
      method: 'POST',
      body: JSON.stringify({ serverId, url }),
    }),
  saveUrl: (serverId: string, url: string) =>
    request<{ selectedServers: PlexServer[] }>('/api/settings/plex/server', {
      method: 'POST',
      body: JSON.stringify({ serverId, url }),
    }),
}

// ── API key (for Radarr and scripts) ──────────────────────────────────────────

export const apiKeyApi = {
  get: () => request<ApiKey>('/api/settings/apikey'),
  regenerate: () =>
    request<ApiKey>('/api/settings/apikey/regenerate', { method: 'POST' }),
}
