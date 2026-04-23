import type {
  Movie, YoutubeResult, PlexServer, PlexLibrary,
  SetupStatus, Settings, SyncStatus, VersionInfo, HistoryEntry, DashboardStats,
} from './types'

const BASE = (process.env.NEXT_PUBLIC_API_URL ?? '').replace(/\/$/, '')

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

  const res = await fetch(`${BASE}${path}`, { ...init, headers })

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
  return res.json()
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

  downloadStatus: (movieId: string) =>
    request<{ inProgress: boolean; finished: boolean; error: string | null; logs: string[] }>(`/api/download/status/${encodeURIComponent(movieId)}`),

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
